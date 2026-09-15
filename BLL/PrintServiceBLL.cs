using InkPrinterCode.Common;
using InkPrinterCode.DAL;
using InkPrinterCode.Model;

namespace InkPrinterCode.BLL
{
    /// <summary>
    /// [2026-09-12] 喷码服务状态机（定时发码 v3 + 取码占用制 + stash 重发）
    ///
    /// 【防重铁律（万总 2026-09-10 17:29 指令，最高优先级 · 取码占用制）】
    ///   从数据库取出码的瞬间立刻标为"已喷"（占用锁），之后才写入喷码机 ——
    ///   重取条件恒为 PrintStatus=0，因此同一条码不可能被第二次取出，
    ///   任何异常路径（写失败 / 库异常 / 应答丢失）都不会导致重复喷印。
    ///   【失败不回退】write 失败也保持已喷（宁可漏喷，绝不重喷）；停止时不回退任何码。
    ///   [2026-09-12] stash 重发为万总裁定的唯一例外：未被确认收下（0x15/超时/写失败）的码
    ///   重发"同一个码"，不是"取第二条码再写"，防重铁律语义不变。
    ///
    /// 【线程结构（全部 IsBackground=true，程序退出不受阻）】
    ///   启动线程    ：连接 → 拉起常驻线程 → 信号设置 → 清三条队列 → 预填缓存（一次性）
    ///   接收线程    ：唯一读流入口，按帧特征分流（防粘包），投递 ACK / 触发打印完成 /
    ///                 清零"连续应答超时"计数（收到机器任何字节即证明对端活着）
    ///   心跳线程    ：【已停用】代码保留、不再启动（万总 2026-09-12 指令）——
    ///                 200ms 定时发码本身就是"写 + 等应答"的探活，密度远高于 3 秒心跳
    ///   发码线程    ：每 200ms（SEND_INTERVAL_MS）定时发一条：stash 非空重发 stash 码，
    ///                 为空则取新码（万总 2026-09-12 指令，取代原"0x32 驱动补码"）
    ///   重连线程    ：断线后按配置间隔重连，成功后重新初始化协议环境并补足缓存（stash 保留）
    ///   停止线程    ：七步停止流程（定时器→线程→清队列→关连接→释放→UI 复原），清空 stash
    ///   测试喷印线程：新建独立连接 → 信号设置 → 清三队列 → 发 AB12345 → 立即关闭并释放
    ///                 （仅"未启动"态可用，与开始/结束喷码互斥，万总 19:53 指令）
    ///
    /// 【两级锁职责】
    ///   _ioLock ：业务层事务锁 —— 包住"发送"这一笔（写字节），等待应答在锁外，
    ///             心跳/发码/测试串行不互堵（万总 13:42 指令）；
    ///   连接层内部锁：只保护流对象不被并发访问（见 IPrinterConnection 接口注释）。
    ///
    /// 【防粘包机制】
    ///   接收线程把字节流灌进 _rxBuffer，按帧特征拆分：0x06/0x15/0x32 是单字节事件；
    ///   0x1B 开头的完整帧读到帧尾 0x04 配对解析；对不上的字节原样十六进制写日志。
    ///   _ioLock 保证同一时刻只有一笔"写"在飞，200ms 节拍又天然串行，应答归属清晰。
    ///
    /// 【定时发码（万总 2026-09-12 指令，最终版 v3，取代原"0x32 驱动补码"）】
    ///   发码线程每 SEND_INTERVAL_MS（200ms，静态常量）定时发一条：
    ///   · stash（_stashCode）非空 → 重发 stash 里那个码（不重新取码、不重新占用、不重复计数）；
    ///   · stash 为空 → 按 Id 顺序取一条新码，取码即标已喷（占用制）。
    ///   【两分支判定】喷码机回 0x06 = 确认收下 → 下轮取新码；
    ///   其他一切（0x15 拒收 / 应答超时 / 写失败）→ 该码进 stash，下轮重发同一个码。
    ///   0x32 打印完成照常接收、照常显示、照常计「喷印数」，但不再驱动发码。
    ///   【发码数量口径】只有新码占用成功才 +1；stash 重发不计数（万总：重发不重复计数）。
    ///   【重喷边界】机器已收下但 0x06 丢失/迟到时，重发会致同码重喷 —— TCP 可靠传输下
    ///   概率极低，断网重连清队列兜底；万总裁定"不烧码（不浪费码）优先"（2026-09-12）。
    ///
    /// 【断线判定（心跳停用后的替代，万总 2026-09-12 指令）】
    ///   每次发码后等应答，连续 SEND_ACK_TIMEOUT_STREAK_MAX 次应答超时（即"写进去却
    ///   毫无反馈"）→ 判定断线 → 走重连流程；接收线程收到机器任何字节立即清零计数。
    ///
    /// 【测试喷印（万总 19:53 指令：完全独立）】
    ///   与生产共用同一份配置，但新建独立连接对象：建连 → 打印信号设置 → 清三条队列 →
    ///   发固定值 AB12345 → 关闭并释放连接。不写数据库、不占生产码 Id。
    ///   测试时生产必已停止（互斥保证），故清队列不会影响任何生产在途数据。
    ///   【释放铁律】无论成功 / 拒收 / 超时 / 异常，finally 必须 Close() + Dispose() 完全释放；
    ///   程序退出（StopSync）也会 Join 测试线程并关闭该测试连接。
    ///
    /// 【日志策略（万总 19:47 / 19:53 / 2026-09-12 指令）】
    ///   落盘（LogHelper）：所有 read/write 异常、协议异常、业务与状态机事件 —— 现场排查证据；
    ///     0x15 拒收 UI 完全静默，落盘按 30 秒汇总限频（"缓存满持续中已拒 N 次"），防刷盘。
    ///   仅 UI（LogDevice）：设备交互过程 —— 0x06 应答 / 0x32 打印完成 / 发码 / 指令成功 /
    ///     测试喷印全部行。操作员据此看清"收反馈 → 再发码"的周期。
    /// </summary>
    public class PrintServiceBLL
    {
        // ============================================================
        // 对 UI 的事件（全部在后台线程触发，UI 端必须 Invoke 回 UI 线程）
        // ============================================================

        /// <summary>运行日志一行（不带时间戳，UI 端自行加时间并倒序显示）</summary>
        public event Action<string>? RunLog;

        /// <summary>生产状态变化：(状态文本, 是否运行中)。UI 据此联动三个按钮</summary>
        public event Action<string, bool>? ServiceStateChanged;

        /// <summary>喷码机状态变化：(文本, 颜色)——未连接/运行中(绿)/断线(红)</summary>
        public event Action<string, System.Drawing.Color>? PrinterStateChanged;

        /// <summary>看板数字变化（发码成功后触发，UI 重新查库刷新）</summary>
        public event Action? DashboardChanged;

        // ============================================================
        // 内部状态
        // ============================================================

        /// <summary>
        /// ACK 等待事务（发码/指令写出后，由接收线程投递结果）。
        /// [2026-09-10] 锁优化方案 1（万总 22:33 批准）后改为三态：
        ///   Received=false → 应答尚未到达（等待超时后据此返回 null，与"收到 0x15"区分开）；
        ///   Received=true  → 已收到，此时 Acked 才有意义（true = 0x06 / false = 0x15）。
        /// 【为什么必须三态】原来只有 Acked 一个 bool，"超时没收到"和"收到 0x15"都表现为 false，
        ///   无法区分；新的锁外等待逻辑要求"先看是否已收到、再决定要不要睡"，故必须能表达"未收到"。
        /// </summary>
        private class PendingAck
        {
            /// <summary>是否已收到应答（false = 接收线程还没投递任何结果）</summary>
            public bool Received = false;

            /// <summary>true = 0x06；false = 0x15（仅当 Received 为 true 时有意义）</summary>
            public bool Acked = false;
        }

        /// <summary>
        /// [2026-09-12] 单条码发送结果（定时发码 v3 两分支判定的依据）
        /// 【Acked】      喷码机回 0x06 确认收下 → 下轮取新码（stash 清空）
        /// 【Nacked】     喷码机回 0x15 拒收（多为机内缓存满）→ 该码进 stash，下轮重发同一个码
        /// 【AckTimeout】 应答超时（写出去了却无任何反馈）→ 该码进 stash 重发；连续 3 次判离线
        /// 【ClaimFailed】占用失败（库异常或该码已被别的路径标过）→ 本条没写出去；
        ///                码未被本次占用，不进 stash，下轮重取自然重试（防重优先）
        /// 【WriteFailed】写入异常（连接有问题）→ 该码进 stash，下轮重发；并触发断线重连
        /// </summary>
        private enum SendResult
        {
            Acked = 0,
            Nacked = 1,
            AckTimeout = 2,
            ClaimFailed = 3,
            WriteFailed = 4
        }

        /// <summary>
        /// [2026-09-12] ★ 定时发码节拍（毫秒）★ —— 万总要求写成静态变量，后续改代码只找这一处。
        /// 发码线程每过 SEND_INTERVAL_MS 发一条码：stash 非空重发 stash 码，为空取新码。
        /// 【取值说明】200ms = 每秒最多 5 条，天然限流（机器缓存满时多余的发送只会收到 0x15，
        ///   限流可防止高频空转）；产线节拍快于 5 瓶/秒时调小此值即可，改完重新编译生效。
        /// </summary>
        private const int SEND_INTERVAL_MS = 200;

        /// <summary>
        /// [2026-09-12] 生产发码等应答超时（毫秒），本次从 100 改为 2000。
        /// 【语义（定时发码 v3）】超时 = "码写出去了却毫无反馈"：该码进 stash 下轮重发（不浪费码），
        ///   且连续 SEND_ACK_TIMEOUT_STREAK_MAX 次超时判离线转重连 —— 心跳已停用，
        ///   本值直接决定断线发现速度：3 × (2000 + 200) ≈ 6.6 秒。
        /// 【为什么给足 2000ms】超时误判的代价是 stash 重发 —— 若机器实际已收下（0x06 只是在途），
        ///   重发会造成同码重喷。正常应答几十毫秒内到达，2000ms 余量充足，宁慢勿误。
        /// 【历史】100ms 是旧方案 B′（等待在 _ioLock 锁内时代）为压持锁时长而设；
        ///   锁优化后等待已在锁外，且新机制下超时会触发 stash 重发，必须给足以防误判。
        /// 【不适用】启动/重连的信号设置与清队列（ExecInstruction）仍用 SendResponseTimeoutMs，
        ///   那些指令必须给足应答时间，不受本常量影响。
        /// </summary>
        private const int SEND_ACK_TIMEOUT_MS = 2000;

        /// <summary>
        /// [2026-09-10] 接收线程单次 Read 的等待超时（毫秒）。
        ///
        /// 【当前语义（Socket.Poll 移出连接锁之后）】此值只决定"无数据时接收线程多久醒一次"，
        ///   影响两件事：一是取消响应速度（停止流程 Join 接收线程的最坏等待）；
        ///   二是空转频率。**不再影响写方（发码 / 心跳）的等锁时间** ——
        ///   连接层的 Poll 已移到锁外，读方持锁只剩"取引用 + 真正读数据"的微秒级。
        ///
        /// 【历史】200 → 50 → 15：早期 Poll 在连接锁内，读方持锁时长 = 写方最坏等待，
        ///   故 2026-09-10 把该值从 50 收到 15 来压缩写方等待。同日 18:35 万总开工把 Poll
        ///   移出锁后，写方等待归零、此值与锁竞争彻底解耦，保留 15 只是让取消响应更快。
        ///
        /// 【为什么不继续往下压】Windows 系统定时器粒度约 15.6ms，Socket.Poll / Thread.Sleep
        ///   的实际最短等待受此限制 —— 设成 10 或 5 基本无效，仍要约 15.6ms 才返回。
        ///
        /// 【代价】无数据时接收线程空转频率约 64 次/秒。Poll 是内核阻塞等待，不占 CPU 计算时间，
        ///   开销可忽略。
        /// </summary>
        private const int RECEIVE_POLL_TIMEOUT_MS = 15;

        private readonly object _stateLock = new object();
        private ServiceState _state = ServiceState.Stopped;

        /// <summary>当前连接（volatile：接收线程读、重连线程换）</summary>
        private volatile IPrinterConnection? _connection;

        /// <summary>
        /// 业务层事务锁：包住"发送"这一笔（写字节）。
        /// [2026-09-10] 方案 1 后 **不再包"等应答"** —— 等待已移到锁外（见 WaitPendingAck），
        ///   持锁时长只剩写字节的微秒级，读 / 写不再互堵。
        /// </summary>
        private readonly object _ioLock = new object();

        /// <summary>ACK 投递锁：接收线程 → 等待中的事务（保护 _pendingAck 的挂箱 / 投递 / 摘箱）</summary>
        private readonly object _ackLock = new object();

        /// <summary>当前在途事务的收件箱（同一时刻至多一个；并发前提见 BeginPendingAck 注释）</summary>
        private PendingAck? _pendingAck = null;

        /// <summary>接收缓冲区（接收线程写入，同锁内解析）</summary>
        private readonly object _rxLock = new object();
        private readonly List<byte> _rxBuffer = new List<byte>();

        /// <summary>
        /// [2026-09-12] 待重发码（stash）：上一次发出但未被喷码机确认收下（0x15/超时/写失败）的码。
        /// 【语义（万总 2026-09-12 定时发码 v3）】发码每 200ms 一轮：stash 非空 → 重发 stash 里
        ///   这个码（不重新取码、不重新占用、发码数量不 +1）；stash 为空 → 取新码。
        ///   两分支判定：机器回 0x06 = 确认收下 → stash 清空，下轮取新码；
        ///   0x15 / 应答超时 / 写失败 → 码留在 stash（或新码放入），下轮重发同一个码。
        /// 【存整条 CodeData】重发日志要带 Id，方便与数据库对账（原配额机制 _pendingSend 已整体删除）。
        /// 【生命周期】启动时清空；重连保留（重连后 stash 码排在预填 3 条之后等腾位，顺序晚几拍不丢）；
        ///   停止时清空（万总裁定：停机烧 1 码没关系）。
        /// 【访问】只有发码线程与启动/重连线程的预填读写（预填期间发码线程被门闩挡住，
        ///   同一时刻至多一个写者），引用赋值本身原子，无需加锁。
        /// </summary>
        private CodeData? _stashCode = null;

        /// <summary>
        /// [2026-09-12] 连续"发码应答超时"计数（心跳停用后的断线判定依据）。
        /// 【机制】发码线程每遇到一次应答超时 +1；接收线程收到机器任何字节立即清零
        ///   （对端能发字节就是活着）；达到 SEND_ACK_TIMEOUT_STREAK_MAX 判定断线转重连。
        /// 【为什么只在接收线程清零】TCP 半开（拔网线/对端死机）时 Write 照样"成功"，
        ///   只有真正读到字节才能证明对端活着 —— 与原心跳"只认读、不认写"同一逻辑。
        /// 【访问】Interlocked（发码线程增、接收线程清零，两个线程）。
        /// </summary>
        private int _ackTimeoutStreak = 0;

        /// <summary>连续应答超时判离线的阈值（万总 2026-09-12 指令：连续 3 次写入后无反馈认为断网）</summary>
        private const int SEND_ACK_TIMEOUT_STREAK_MAX = 3;

        /// <summary>预填条数（启动/重连时一次性发几条；运行中不再作为发码门限使用）</summary>
        private int _targetCache = 3;

        /// <summary>喷码机在线标志（心跳超时置 false，重连成功置 true）</summary>
        private volatile bool _printerOnline = false;

        /// <summary>重连互斥标志：0=空闲 1=重连中（防止心跳与发码同时触发两条重连线程）</summary>
        private int _reconnectFlag = 0;

        /// <summary>测试喷印互斥标志：0=空闲 1=执行中（防连点）</summary>
        private int _testFlag = 0;

        /// <summary>
        /// [2026-09-10] 最后一次"成功读到数据"的时刻（Environment.TickCount64，单调递增，不受系统改时间影响）。
        ///
        /// 【只认读、不认写（万总 19:53 裁定）】TCP 半开 / 网线被拔时 Write 仍会返回成功
        ///   （数据只进了本机内核发送缓冲，谁也证明不了对端收到）。若把写成功也计入，
        ///   则"设备已死但 socket 挂着"时本值一直被刷新 → 心跳永不执行 → 永久发现不了断线。
        ///   只有真正读到字节，才证明对端还活着。
        ///
        /// 【用途】心跳线程据此跳过探活：空闲 &lt; 心跳间隔 → 说明刚有数据交互，本轮不必发查询帧。
        ///   产线连续生产时每码必回 0x06 + 0x32，读持续有数据 → 心跳基本不执行（万总设计目的）。
        ///
        /// 【访问】写入走 Interlocked.Exchange，读取走 Interlocked.CompareExchange(...,0,0)，跨线程可见。
        /// </summary>
        private static long _lastIoMs = 0;

        /// <summary>"库中无可用码"是否已提示过（只提示一次，避免刷屏）</summary>
        private bool _noCodeLogged = false;

        /// <summary>
        /// [2026-09-10] 本次运行「发码数量」—— 走到 Write 一次就 +1，无论写入成功与否，也不看任何反馈。
        /// 【口径（万总 20:23 指令）】以"本次下发动作"为界：只要进入写入分支就计数，
        ///   写失败 / 被 0x15 拒收同样计入；用来和「喷印数」对照出"发出去但还没喷出来"的在途量
        ///   （万总明确：有数据差不要紧）。
        /// 【归零】每次 Start() 归零；断线重连不归零（仍属同一次运行）。
        /// 【访问】后台线程 Interlocked 自增，UI 线程经 RunSendCount 属性原子读（CompareExchange）。
        /// </summary>
        private int _runSendCount = 0;

        /// <summary>
        /// [2026-09-10] 本次运行「喷印数」—— 只认喷码机回传的 0x32（打印完成），别的一概不算。
        /// 【口径（万总 20:23 指令）】写失败 / 被 0x15 拒收 / 预填进机内尚未喷出的一律不计。
        /// 【归零】每次 Start() 归零；断线重连不归零（仍属同一次运行）。
        /// 【访问】接收线程 Interlocked 自增，UI 线程经 RunPrintedCount 属性原子读（CompareExchange）。
        /// </summary>
        private int _runPrintedCount = 0;

        /// <summary>
        /// [2026-09-11] 「可用码基数」（看板与查库解耦 D1，万总指令）。
        /// 【机制】每次启动在 PrefillCache 之前查一次未喷总数锁定为基数；
        ///   运行中看板可用数 = 基数 − 发码数量（RunSendCount），运行期间不再查库。
        ///   依据：SendOneCode 里 MarkPrinted 成功必伴随发码数量 +1、失败则两边都不动，
        ///   未喷库存的减少量 ≡ 发码数量，逐条恒等，相减结果即库中真实可用数。
        /// 【值语义】−1 = 未知（尚未锁定 / 启动时查询失败），UI 据此回退实时查库。
        /// 【归零】每次 Start() 置 −1；重连不重取（重连补填同样"未喷−3、发码+3"两边同步动，
        ///   恒等式不破；若重连重取基数反而会与发码数量重复扣减）。
        /// 【访问】启动线程 Interlocked 写，UI 线程经 AvailableBase 属性原子读（CompareExchange）。
        /// </summary>
        private int _availableBase = -1;

        /// <summary>上次记录的喷码机状态码（状态码变化才记日志，避免每 3 秒刷一条）</summary>
        private int _lastLoggedStatusCode = -1;

        /// <summary>状态帧到达信号（心跳线程等待用）</summary>
        private readonly AutoResetEvent _statusFrameEvent = new AutoResetEvent(false);

        /// <summary>
        /// 打印完成事件信号。
        /// [2026-09-12] 发码已改 200ms 定时驱动，发码线程不再等待本信号（OnPrintDone 仍 Set）；
        ///   字段保留，恢复事件驱动时直接可用。
        /// </summary>
        private readonly AutoResetEvent _printDoneEvent = new AutoResetEvent(false);

        /// <summary>取消信号（停止流程第一步置消）</summary>
        private CancellationTokenSource? _cts = null;

        // 常驻线程引用（停止流程 Join 用）
        private Thread? _receiveThread = null;
        private Thread? _heartbeatThread = null;
        private Thread? _sendThread = null;
        private Thread? _reconnectThread = null;
        private Thread? _workerThread = null;

        /// <summary>[2026-09-10] 测试喷印线程引用（程序退出时 Join，确保测试连接被收尾）</summary>
        private Thread? _testPrintThread = null;

        /// <summary>
        /// [2026-09-10] 测试喷印的独立连接对象（与生产连接 _connection 完全分离）。
        /// 供停止流程 / 程序退出兜底关闭；测试线程自身的 finally 也会关闭它，重复关闭安全。
        /// </summary>
        private IPrinterConnection? _testConnection = null;

        /// <summary>服务状态枚举</summary>
        public enum ServiceState
        {
            /// <summary>未启动（唯一允许点「开始喷码」的状态）</summary>
            Stopped = 0,
            /// <summary>启动中（连接/预填过程中，按钮全锁）</summary>
            Starting = 1,
            /// <summary>运行中（结束喷码可用；测试喷印不可用）</summary>
            Running = 2,
            /// <summary>停止中（停止流程执行期间，按钮全锁）</summary>
            Stopping = 3,
            /// <summary>测试喷印中（独立连接；与开始/结束喷码互斥，按钮全锁）</summary>
            Testing = 4
        }

        // ============================================================
        // 对外接口
        // ============================================================

        /// <summary>当前是否处于运行中（含断线重连期间 —— 服务仍在跑，只是连接断了）</summary>
        public bool IsRunning
        {
            get
            {
                lock (_stateLock)
                {
                    return _state == ServiceState.Running;
                }
            }
        }

        /// <summary>
        /// 本次运行「发码数量」（走到 Write 就 +1，无论成败、不看反馈），看板显示用（原子读）。
        /// 口径见 _runSendCount 字段说明。
        /// </summary>
        public int RunSendCount
        {
            get
            {
                return System.Threading.Interlocked.CompareExchange(ref _runSendCount, 0, 0);
            }
        }

        /// <summary>
        /// 本次运行「喷印数」（只认机器回传 0x32），看板显示用（原子读）。
        /// 口径见 _runPrintedCount 字段说明。
        /// </summary>
        public int RunPrintedCount
        {
            get
            {
                return System.Threading.Interlocked.CompareExchange(ref _runPrintedCount, 0, 0);
            }
        }

        /// <summary>
        /// 可用码基数（−1 = 未知），看板显示用（原子读）。口径见 _availableBase 字段说明。
        /// </summary>
        public int AvailableBase
        {
            get
            {
                return System.Threading.Interlocked.CompareExchange(ref _availableBase, -1, -1);
            }
        }

        /// <summary>
        /// 开始喷码（万总规则：btnStart / btnStop enable 互斥，由 UI 事件联动保证）
        /// 【机制】只拉启动线程，不阻塞 UI —— 连接失败/预填失败时回"未启动"并弹日志说明。
        /// </summary>
        public void Start()
        {
            lock (_stateLock)
            {
                if (_state != ServiceState.Stopped)
                {
                    return;
                }
                _state = ServiceState.Starting;
            }

            // [2026-09-10] 万总 20:23 指令：看板两个计数每次启动都从 0 开始
            //   （「发码数量」与「喷印数」；断线重连不归零，仍属同一次运行）
            // [2026-09-11] 可用码基数一并置 −1（未知）：基数由启动线程在 StartSequence
            //   预填之前重新锁定，与发码数量共用同一起点，"基数 − 发码数量"口径才对齐。
            System.Threading.Interlocked.Exchange(ref _runSendCount, 0);
            System.Threading.Interlocked.Exchange(ref _runPrintedCount, 0);
            System.Threading.Interlocked.Exchange(ref _availableBase, -1);

            FireServiceState("启动中", false);
            LogInfo("【喷码服务】正在启动……");

            _workerThread = new Thread(StartSequence);
            _workerThread.IsBackground = true;
            _workerThread.Name = "PrintService-Start";
            _workerThread.Start();
        }

        /// <summary>
        /// 结束喷码（七步停止流程在后台线程执行，UI 收到"未启动"事件即复原）
        /// </summary>
        public void Stop()
        {
            lock (_stateLock)
            {
                if (_state != ServiceState.Running && _state != ServiceState.Starting)
                {
                    return;
                }
                _state = ServiceState.Stopping;
            }

            FireServiceState("停止中", false);
            LogInfo("【喷码服务】正在停止……");

            _workerThread = new Thread(delegate () { StopSteps("手动停止"); });
            _workerThread.IsBackground = true;
            _workerThread.Name = "PrintService-Stop";
            _workerThread.Start();
        }

        /// <summary>
        /// 同步停止（程序退出专用）：在调用线程直接执行停止流程，确保退出前连接全部关闭。
        /// 【边界】 MainForm 关闭时调用；各步骤有超时，最坏几秒内返回。
        /// </summary>
        public void StopSync()
        {
            lock (_stateLock)
            {
                if (_state == ServiceState.Stopped)
                {
                    return;
                }
                _state = ServiceState.Stopping;
            }

            StopSteps("程序退出");

            lock (_stateLock)
            {
                _state = ServiceState.Stopped;
            }
            FireServiceState("未启动", false);
        }

        /// <summary>
        /// 测试喷印（万总 19:53 指令：完全独立，仅"未启动"态可用）。
        /// 【互斥】开始喷码只能从未启动态发起、结束喷码只能在运行/启动中发起，而本方法只在
        ///   "未启动"态放行 → 测试与开始、测试与结束天然互斥（UI 层再禁按钮，双保险）。
        /// 【流程】只置状态并拉起测试线程；真正的建连/发码/释放都在 TestPrintSequence 内完成。
        /// </summary>
        public void TestPrint()
        {
            // 只有完全空闲（未启动）才允许测试：运行中 / 启动中 / 停止中 / 测试中一律拒绝
            lock (_stateLock)
            {
                if (_state != ServiceState.Stopped)
                {
                    LogDevice("【测试喷印】当前状态不允许测试（请先点「结束喷码」回到未启动再试）。");
                    return;
                }
                _state = ServiceState.Testing;
            }

            // 互斥防连点：已有测试在执行则忽略本次点击
            if (System.Threading.Interlocked.CompareExchange(ref _testFlag, 1, 0) != 0)
            {
                lock (_stateLock)
                {
                    _state = ServiceState.Stopped;
                }
                return;
            }

            FireServiceState("测试中", false);

            _testPrintThread = new Thread(TestPrintSequence);
            _testPrintThread.IsBackground = true;
            _testPrintThread.Name = "PrintService-TestPrint";
            _testPrintThread.Start();
        }

        // ============================================================
        // 启动流程（启动线程执行）
        // ============================================================

        /// <summary>
        /// 启动序列：读配置 → 建连接 → 协议初始化 → 拉起常驻线程 → 预填缓存 → 运行中。
        /// 任何一步失败：走停止流程回收资源，状态回"未启动"。
        /// </summary>
        private void StartSequence()
        {
            try
            {
                _cts = new CancellationTokenSource();
                // [2026-09-12] 定时发码 v3：stash 与连续超时计数随启动归零（原配额 ResetPendingSend 已删除）
                _stashCode = null;
                System.Threading.Interlocked.Exchange(ref _ackTimeoutStreak, 0);
                _targetCache = ConfigHelper.InitialCacheCount;
                _printerOnline = false;
                _noCodeLogged = false;
                _lastLoggedStatusCode = -1;

                // ---------- 1. 读启用配置 ----------
                PrinterConfigBLL.LoadResult load = PrinterConfigBLL.Load();
                if (!load.Success)
                {
                    throw new InvalidOperationException(load.Message);
                }

                // ---------- 2. 建连接 ----------
                _connection = CreateConnection(load);
                _connection.Open();
                LogInfo("【连接】已建立：" + DescribeConnection(load));

                // ---------- 3. 拉起常驻线程 ----------
                // [2026-09-10] P2 修复：线程必须先于协议初始化拉起 —— 否则下面 4 条指令
                //   发出后没有接收线程读流，每条都白等满 SendResponseTimeoutMs（默认 3 秒），
                //   启动固定慢约 12 秒且日志出现误导性"应答超时"。
                //   安全性：心跳/发码线程循环内有 IsRunning（Starting 时为 false）与
                //   _printerOnline（启动成功前为 false）双重门闩，拉起后只会休眠，
                //   不会在预填完成前抢先发任何数据。
                StartThreads();

                // ---------- 4. 协议环境初始化：打印信号设置 + 清三条缓存队列 ----------
                // 【线程归属】本步骤与下面的预填发码均在启动线程（PrintService-Start）上顺序执行，
                //   与"连续发 3 个码"是同一条线程的前后两段，天然串行、不存在争抢；
                //   心跳/发码两条常驻线程此刻被双门闩挡在循环口，一次都不会发数据。
                ExecInstruction(CodeNetProtocol.BuildSignalSetupFrame(), "打印信号设置");
                ExecInstruction(CodeNetProtocol.BuildClearQueueFrame(0), "清 TCP/IP 缓存队列");
                ExecInstruction(CodeNetProtocol.BuildClearQueueFrame(1), "清 RS232 缓存队列");
                ExecInstruction(CodeNetProtocol.BuildClearQueueFrame(2), "清历史缓存队列");

                // ---------- 4.5 锁定「可用码基数」（看板与查库解耦 D1，万总 2026-09-11 指令） ----------
                // 【机制】启动时查一次未喷总数作基数；此后运行中看板可用数 = 基数 − 发码数量，
                //   运行期间一条库都不查（几千万行数据下界面也不再被 COUNT 拖慢）。
                // 【位置】必须在本步（4.5）与第 5 步 PrefillCache() 之间 —— 预填取码会把
                //   未喷 −3、发码数量 +3（两边同步动，恒等式"未喷减量 ≡ 发码数量"不破），
                //   但基数若取在预填之后就会把预填那 3 条重复扣一遍，可用数长期虚低 3。
                // 【失败】取基数失败只置 −1（UI 回退实时查库），绝不因它中断启动。
                try
                {
                    int baseCount = CodeDataDAL.GetStatusCount(PrintStatus.NotPrinted);
                    System.Threading.Interlocked.Exchange(ref _availableBase, baseCount);
                    LogInfo("【看板】可用码基数已锁定：" + baseCount.ToString()
                            + " 条；运行中改为\"基数 − 发码数量\"相减显示，不再查库");
                }
                catch (Exception ex)
                {
                    System.Threading.Interlocked.Exchange(ref _availableBase, -1);
                    LogWarn("【看板】取可用码基数失败，运行中回退实时查库：" + ex.Message);
                }

                // ---------- 5. 预填缓存（按 Id 顺序取码；取码即标已喷，占用制） ----------
                // [2026-09-10] P3-2 说明：预填与发码线程不会并发取到同一条未喷码 ——
                //   依赖上面的双重门闩：预填期间 _state=Starting（IsRunning=false）且
                //   _printerOnline=false，发码线程在循环口即被挡住；预填全部结束后才进入运行态。
                //   若日后调整本方法的步骤顺序，务必保持这两道门闩始终先于 PrefillCache 关闭。
                int prefilled = PrefillCache();

                // ---------- 6. 进入运行态 ----------
                lock (_stateLock)
                {
                    _state = ServiceState.Running;
                }
                _printerOnline = true;

                FirePrinterState("运行中", System.Drawing.Color.Green);
                FireServiceState("运行中", true);
                LogInfo("【喷码服务】启动成功：预填 " + prefilled.ToString()
                        + " 条；此后每 " + SEND_INTERVAL_MS.ToString()
                        + " 毫秒定时发一条码，0x06 收下即发下一条，0x15/超时则重发同一个码");
            }
            catch (Exception ex)
            {
                LogError("【喷码服务】启动失败：" + ex.Message);
                LogHelper.Instance.Error("喷码服务启动失败", ex);

                // 回收资源回"未启动"，让万总可以改完配置重新开始
                StopSteps("启动失败回滚");
            }
        }

        /// <summary>按启用配置创建连接对象（启动与重连共用，重连时读的是最新配置）</summary>
        private IPrinterConnection CreateConnection(PrinterConfigBLL.LoadResult load)
        {
            if (load.EnabledType == Model.Enums.ConnType.Tcp)
            {
                bool resetEnabled = ConfigHelper.TcpResetEnabled && load.TcpConfig.ResetPort > 0;
                return new TcpPrinterConnection(load.TcpConfig.TcpIp, load.TcpConfig.TcpPort,
                                                resetEnabled, load.TcpConfig.ResetPort);
            }
            else
            {
                return new SerialPrinterConnection(load.SerialConfig.SerialPortName,
                                                   load.SerialConfig.SerialBaudRate);
            }
        }

        /// <summary>连接的可读描述（日志用）</summary>
        private string DescribeConnection(PrinterConfigBLL.LoadResult load)
        {
            if (load.EnabledType == Model.Enums.ConnType.Tcp)
            {
                return "TCP " + load.TcpConfig.TcpIp + ":" + load.TcpConfig.TcpPort.ToString();
            }
            return "串口 " + load.SerialConfig.SerialPortName + " @" + load.SerialConfig.SerialBaudRate.ToString();
        }

        /// <summary>
        /// 拉起常驻线程（接收 / 发码），全部 IsBackground=true（万总硬性要求）。
        /// [2026-09-12] 心跳线程停用（万总指令：代码保留、不再启动）—— 发码每 200ms 一轮，
        ///   每轮本身就是"写 + 等应答"的探活，连续 SEND_ACK_TIMEOUT_STREAK_MAX 次应答超时
        ///   即判断线（见 SendLoop），探活密度（0.2 秒级）远高于原心跳（3 秒间隔）。
        ///   停止流程 JoinThread(_heartbeatThread) 保留：引用为 null 时安全跳过。
        /// </summary>
        private void StartThreads()
        {
            _receiveThread = new Thread(ReceiveLoop);
            _receiveThread.IsBackground = true;
            _receiveThread.Name = "PrintService-Receive";
            _receiveThread.Start();

            // [2026-09-12] 万总指令：心跳线程停用（代码保留、不启动）。原启动代码：
            // _heartbeatThread = new Thread(HeartbeatLoop);
            // _heartbeatThread.IsBackground = true;
            // _heartbeatThread.Name = "PrintService-Heartbeat";
            // _heartbeatThread.Start();

            _sendThread = new Thread(SendLoop);
            _sendThread.IsBackground = true;
            _sendThread.Name = "PrintService-Send";
            _sendThread.Start();
        }

        /// <summary>
        /// 预填缓存：按 Id 顺序取 N 条未喷码逐条发出（启动流程调用）。
        /// [2026-09-12] 定时发码 v3 下的新语义：
        /// 【Acked】正常发出 → 继续下一条；
        /// 【0x15/超时/写失败】该码已被 SendOneCode 放进 stash（运行后每 200ms 自动重发），
        ///   预填到此为止、不再发后续新码，但启动/重连流程继续 —— 旧版"直接中止"已无必要
        ///   （stash 保证了这条码不丢，断线场景由运行态的连续超时判定接管重连）；
        /// 【ClaimFailed】占用失败（库异常/码被标过）→ 仍抛异常中止（防重铁律优先，
        ///   占用失败说明取码路径有问题，继续发后面的码没有意义）。
        /// </summary>
        /// <returns>实际发出条数（仅用于日志显示；进 stash 的码同样已标已喷，但不含在返回值内）</returns>
        private int PrefillCache()
        {
            List<CodeData> codes = CodeDataDAL.GetNotPrintedCodes(_targetCache);

            if (codes.Count == 0)
            {
                LogWarn("【预填】库中没有未喷码，服务进入运行态等待导入。");
                return 0;
            }

            int sent = 0;

            for (int i = 0; i < codes.Count; i++)
            {
                SendResult result = SendOneCode(codes[i], false);

                if (result == SendResult.ClaimFailed)
                {
                    throw new InvalidOperationException("预填缓存时取码占用失败（数据库异常或码已被占用），启动中止。");
                }

                if (result != SendResult.Acked)
                {
                    // 0x15 / 应答超时 / 写失败：该码已进 stash，运行态每 200ms 自动重发，预填提前收尾
                    LogWarn("【预填】第 " + (i + 1).ToString() + " 条未获喷码机确认（0x15/超时/写失败），"
                            + "该码已转 stash 待自动重发，剩余预填取消，启动流程继续。");
                    break;
                }

                sent++;
            }

            return sent;
        }

        // ============================================================
        // 0x15 拒收日志限频（万总 2026-09-12 指令：UI 静默 + 落盘 30 秒汇总）
        // ============================================================

        /// <summary>0x15 落盘汇总的间隔（毫秒）：任意 30 秒窗口内最多落盘一条汇总</summary>
        private const int NACK_SUMMARY_INTERVAL_MS = 30000;

        /// <summary>上次落盘 0x15 汇总的时刻（TickCount64；0 = 从未落盘，首条立即记）</summary>
        private long _lastNackLogMs = 0;

        /// <summary>距上次汇总累计的 0x15 次数</summary>
        private int _nackCountSinceLog = 0;

        /// <summary>
        /// 0x15 拒收到达（接收线程调用）：UI 完全静默，落盘按 30 秒汇总限频。
        /// 【背景（万总 2026-09-12 指令）】缓存满时机器对每条多余的发送都回 0x15，
        ///   逐条记日志会刷屏刷盘；改为累计计数、每 30 秒落盘一条汇总（含累计次数）。
        /// 【被拒的码去哪】进入 _stashCode，发码线程每 200ms 重发，腾位后自动补进缓存 —— 无码损耗。
        /// 【调用者】只有接收线程（ProcessRxBuffer 的 NAK 分支），字段无需加锁。
        /// </summary>
        private void OnNakReceived()
        {
            _nackCountSinceLog++;

            long now = Environment.TickCount64;

            if (now - _lastNackLogMs >= NACK_SUMMARY_INTERVAL_MS)
            {
                LogHelper.Instance.Warn("【应答】0x15 喷码机拒收（多为机内缓存已满，被拒的码已留在 stash 自动重发），"
                        + "近段累计拒收 " + _nackCountSinceLog.ToString() + " 次（30 秒汇总一条，UI 不显示）");
                _lastNackLogMs = now;
                _nackCountSinceLog = 0;
            }
        }

        /// <summary>
        /// 刷新"最后一次成功读到数据"的时刻（接收线程读到 n&gt;0 时调用）。
        /// 【只认读】详见 _lastIoMs 字段说明；写入走 Interlocked.Exchange，保证跨线程可见。
        /// [2026-09-12] 心跳线程已停用，本方法随心跳代码一并保留（HeartbeatLoop 恢复启动即自动生效）。
        /// </summary>
        private void MarkIoActivity()
        {
            System.Threading.Interlocked.Exchange(ref _lastIoMs, Environment.TickCount64);
        }

        // ============================================================
        // 发码（定时发码 v3 核心：取码占用制 + stash 重发 + 两分支判定）
        // ============================================================

        /// <summary>
        /// 发送一条生产码：新码先占用（标已喷）→ 单次写入 → 等应答 → 按 0x06/其他 两分支收尾。
        ///
        /// 【占用制（万总 2026-09-10 17:29 指令）】新码第一步就标为已喷，之后才写入喷码机。
        ///   取码条件恒为 PrintStatus=0，因此本条一旦标记成功，任何路径都不可能再取到它 ——
        ///   从根上杜绝"重复取码"型重复喷印。
        /// [2026-09-12] 【stash 重发（万总 2026-09-12 定时发码 v3）】isResend=true 表示本条是
        ///   stash 里那个"未被确认收下"的码：不重新占用（早已标已喷）、发码数量不 +1（重发不重复计数），
        ///   只做"写入 + 等应答"。stash 的置/清收尾统一在本方法内完成，调用方无需操心：
        ///   · 0x06 确认收下 → stash 清空（isResend 时）→ 返回 Acked，下轮取新码；
        ///   · 0x15 / 应答超时 / 写失败 → 新码放入 stash（isResend 时保持不动）→ 下轮重发同一个码
        ///     （不浪费码，万总裁定）。
        /// 【发码数量口径（万总 2026-09-12 修订）】只有新码占用成功才 +1（占用 ≡ 未喷库存减量，
        ///   看板"基数 − 发码数量"恒等式保持）；stash 重发不计数。
        /// 【日志】0x06 只进 UI（LogDevice）；0x15 UI 静默（限频汇总在接收线程 OnNakReceived）；
        ///   写失败落盘（LogError）。
        /// </summary>
        /// <param name="code">要发送的码</param>
        /// <param name="isResend">true = stash 重发（不占用、不计数）；false = 新码（占用 + 计数）</param>
        /// <returns>Acked / Nacked / AckTimeout / ClaimFailed / WriteFailed（语义见 SendResult）</returns>
        private SendResult SendOneCode(CodeData code, bool isResend)
        {
            // ---------- 1. 新码：取码即标（占用锁）+ 发码数量 +1（stash 码跳过本步） ----------
            if (!isResend)
            {
                // 只有从未写出过的码能被标记（DAL 内 WHERE PrintStatus = 0）：
                //   · 标记成功（>0）         → 本码归本次发送独占，继续写入
                //   · 标记返回 0             → 该码已被别的路径标过（理论不该发生），不发，防重
                //   · 标记抛异常（库写不了） → 无法保证防重，绝不发送（宁可漏喷，不冒重喷风险）
                int marked;

                try
                {
                    marked = CodeDataDAL.MarkPrinted(code.Id, DateTime.Now.ToDbTimeString(), string.Empty);
                }
                catch (Exception ex)
                {
                    LogHelper.Instance.Error("取码占用失败（Id=" + code.Id.ToString() + "），本条不发送", ex);
                    LogError("【发码】" + code.CodeValue + "（Id=" + code.Id.ToString()
                             + "）取码占用失败，为防重复喷印本次不发送，请检查数据库！");
                    return SendResult.ClaimFailed;
                }

                if (marked <= 0)
                {
                    LogWarn("【发码】" + code.CodeValue + "（Id=" + code.Id.ToString()
                            + "）已被标记为已喷（疑似重复取码路径），为防重复喷印本次不发送，请关注日志排查来源！");
                    return SendResult.ClaimFailed;
                }

                // [2026-09-12] 发码数量 +1：只认新码占用成功（stash 重发不计数 —— 万总指令）。
                //   与"未喷库存减量 ≡ 发码数量"恒等式对齐（看板"基数 − 发码数量"口径）。
                System.Threading.Interlocked.Increment(ref _runSendCount);
            }

            // ---------- 2. 单次写入 + 等应答（新码 / stash 码都只走这一步，同一把 _ioLock） ----------
            byte[] frame = CodeNetProtocol.BuildCacheDataFrame(code.CodeValue);

            try
            {
                bool? ackResult;

                // [2026-09-10] 锁优化方案 1（万总 22:33 批准）：
                //   顺序为「先挂待收件箱 → 锁内写入 → 锁外等待 → finally 摘箱」，
                //   应答一到即可投递唤醒，等待不占 _ioLock。
                PendingAck pending = BeginPendingAck();

                try
                {
                    lock (_ioLock)
                    {
                        EnsureConnection().Write(frame);
                    }

                    ackResult = WaitPendingAck(pending, SEND_ACK_TIMEOUT_MS);
                }
                finally
                {
                    EndPendingAck(pending);
                }

                FireDashboard();

                if (ackResult == true)
                {
                    // 0x06 = 机器确认收下：stash 码任务完成（清空），下轮取新码
                    if (isResend)
                    {
                        _stashCode = null;
                    }

                    // [2026-09-10] 万总 19:53 指令：发码信息只进 UI（不落盘）——
                    //   操作员在界面能看到"发一条 → 收下一条"的节奏（0x06 显示：万总 2026-09-12 指令）
                    LogDevice("【发码】" + code.CodeValue + "（Id=" + code.Id.ToString()
                              + (isResend ? "）stash 重发已被喷码机接收（0x06）"
                                          : "）已被喷码机接收（0x06），状态→已喷"));
                    return SendResult.Acked;
                }

                // 走到这里 = 0x15 拒收 或 应答超时：该码进 stash，下轮重发同一个码（不浪费码）
                if (!isResend)
                {
                    _stashCode = code;
                }

                if (ackResult == false)
                {
                    // 0x15 = 拒收（多为机内缓存已满）：UI 完全静默（万总 2026-09-12 指令），
                    //   落盘汇总由接收线程 OnNakReceived 按 30 秒限频完成，这里不重复记日志
                    return SendResult.Nacked;
                }

                // 应答超时 = "写进去了却毫无反馈"：连续超时计数与判离线由 SendLoop 处理
                return SendResult.AckTimeout;
            }
            catch (Exception ex)
            {
                // 写失败（连接问题）：码保持已喷不回退（A 方案铁律），但转 stash 下轮重发
                // （万总 2026-09-12 指令：写失败也重发同一个码，不浪费码）；
                // 断线重连由 SendLoop 触发，重连后 stash 保留、排在预填之后自动补发
                if (!isResend)
                {
                    _stashCode = code;
                }

                LogHelper.Instance.Error("发码写入失败（Id=" + code.Id.ToString()
                                         + "，码=" + code.CodeValue + "）", ex);
                LogError("【发码】" + code.CodeValue + " 写入失败（" + ex.Message
                         + "）：该码已转 stash 待重发（不浪费码），请关注连接状态！");

                FireDashboard();
                return SendResult.WriteFailed;
            }
        }

        /// <summary>
        /// 挂上"待收件箱"（**必须在 Write 之前调用**）。
        /// [2026-09-10] 锁优化方案 1（万总 22:33 批准）：顺序必须是
        ///   「先挂箱 → 再写入 → 锁外等待 → finally 摘箱」。
        /// 【为什么必须先挂箱】若先写入、再挂箱，应答可能在两者之间到达 —— 那一刻 _pendingAck 还是 null，
        ///   接收线程（DeliverAck）找不到收件人，该应答被丢弃；本事务随后挂上箱子却永远等不到结果，
        ///   表现为"偶发超时"，比原来的"固定超时"更难排查。
        /// 【前提】同一时刻只允许一个事务在途（本字段是单个引用）。当前由调用点分布 +
        ///   _state / _printerOnline 门闩共同保证：发码线程、启动预填、重连预填、启动/重连指令互不并发。
        ///   若日后新增并发调用点，需改为"按事务分配独立收件箱"。
        /// </summary>
        private PendingAck BeginPendingAck()
        {
            PendingAck pending = new PendingAck();

            lock (_ackLock)
            {
                _pendingAck = pending;
            }

            return pending;
        }

        /// <summary>
        /// 摘掉"待收件箱"（无论成功失败都要调用，放在 finally 里）。
        /// 【边界】只摘自己挂的那一个 —— 避免把后续事务挂上的箱子误摘掉。
        /// </summary>
        private void EndPendingAck(PendingAck pending)
        {
            lock (_ackLock)
            {
                if (object.ReferenceEquals(_pendingAck, pending))
                {
                    _pendingAck = null;
                }
            }
        }

        /// <summary>
        /// 在 **_ioLock 之外** 等待接收线程投递 ACK。
        /// [2026-09-10] 锁优化方案 1（万总 22:33 批准）：
        ///   原实现（WaitAckInsideIoLock）把等待放进 lock(_ioLock) 内，而接收线程读到应答字节后
        ///   必须先取得同一把 _ioLock 才能投递 —— 等待者持锁、投递者求锁，应答永远送不进来，
        ///   必然等满超时。后果：生产发码每条白等 100ms；启动 4 条指令共白等约 12 秒。
        /// 【机制】先看 Received 是否已置（应答可能早于本方法进入等待就已到达，例如写出后极短时间
        ///   内机器就回了 0x06）；已置则直接取结果，不进 Wait。未置才睡等，睡醒后回到循环再判 ——
        ///   这样即使 Monitor.PulseAll 早于 Monitor.Wait（经典的"信号丢失"）也不会误判超时。
        /// 【边界】循环到期限仍未收到 → 返回 null（超时无应答），语义与旧实现一致。
        /// </summary>
        /// <returns>true = 0x06；false = 0x15；null = 超时无应答</returns>
        private bool? WaitPendingAck(PendingAck pending, int timeoutMs)
        {
            long deadline = Environment.TickCount64 + (timeoutMs < 1 ? 1 : timeoutMs);

            lock (_ackLock)
            {
                while (!pending.Received)
                {
                    long remain = deadline - Environment.TickCount64;

                    if (remain <= 0)
                    {
                        break;
                    }

                    Monitor.Wait(_ackLock, (int)remain);
                }
            }

            if (!pending.Received)
            {
                return null;
            }

            return pending.Acked;
        }

        /// <summary>
        /// 发送协议指令并等应答（打印信号设置 / 清队列 / 测试喷印共用）。
        /// 【v5 边界】应答结果只进日志 —— 指令层 NAK/超时不改变任何码的状态。
        /// </summary>
        private void ExecInstruction(byte[] frame, string title)
        {
            try
            {
                bool? ackResult;

                // [2026-09-10] 锁优化方案 1（万总 22:33 批准）：先挂箱 → 锁内写入 → 锁外等待 → finally 摘箱。
                //   原来把等待放在 lock(_ioLock) 内，接收线程读应答要抢同一把锁 → 每条约白等满
                //   SendResponseTimeoutMs（默认 3000ms），4 条启动指令合计约 12 秒。
                PendingAck pending = BeginPendingAck();

                try
                {
                    lock (_ioLock)
                    {
                        EnsureConnection().Write(frame);
                    }

                    ackResult = WaitPendingAck(pending, ConfigHelper.SendResponseTimeoutMs);
                }
                finally
                {
                    EndPendingAck(pending);
                }

                if (ackResult == true)
                {
                    LogDevice("【指令】" + title + "：已发送，喷码机应答 0x06（成功）");
                }
                else if (ackResult == false)
                {
                    LogWarn("【指令】" + title + "：喷码机应答 0x15（拒收，请检查指令/模板）");
                }
                else
                {
                    LogWarn("【指令】" + title + "：应答超时（" + ConfigHelper.SendResponseTimeoutMs.ToString() + " 毫秒）");
                }
            }
            catch (Exception ex)
            {
                LogWarn("【指令】" + title + "：发送异常 —— " + ex.Message);
            }
        }

        /// <summary>
        /// 发送一条指令并直接等应答（**测试喷印专用**：连接是临时独立的，不经过常驻接收线程）。
        /// 【与 ExecInstruction 的区别】后者依赖常驻接收线程投递 ACK、且写 _connection 字段；
        ///   测试喷印未启动常驻线程，必须自己读写这条临时连接，故单独实现。
        /// 【实现】写出去后按 RECEIVE_POLL_TIMEOUT_MS 轮询直读，直到认到 0x06 / 0x15 或累计超时。
        /// 【日志】发送与应答全部只进 UI（LogDevice，万总 19:53 指令），便于操作员看懂交互周期。
        /// </summary>
        /// <returns>true = 0x06；false = 0x15；null = 超时无应答</returns>
        private bool? WaitAckDirect(IPrinterConnection conn, byte[] frame, string title)
        {
            conn.Write(frame);

            int timeoutMs = ConfigHelper.SendResponseTimeoutMs;
            int waited = 0;
            byte[] buffer = new byte[256];

            while (waited < timeoutMs)
            {
                int n = conn.Read(buffer, RECEIVE_POLL_TIMEOUT_MS);
                waited += RECEIVE_POLL_TIMEOUT_MS;

                if (n <= 0)
                {
                    continue;
                }

                // 测试场景只需认出首个有意义的事件字节（无需完整帧解析）
                for (int i = 0; i < n; i++)
                {
                    byte b = buffer[i];

                    if (b == CodeNetProtocol.ACK)
                    {
                        LogDevice("【测试喷印】" + title + "：已发送，喷码机应答 0x06（成功）");
                        return true;
                    }

                    if (b == CodeNetProtocol.NAK)
                    {
                        LogDevice("【测试喷印】" + title + "：喷码机应答 0x15（拒收）");
                        return false;
                    }
                }
            }

            LogDevice("【测试喷印】" + title + "：应答超时（" + timeoutMs.ToString() + " 毫秒）");
            return null;
        }

        /// <summary>取当前连接，不可用时抛异常（调用方按写入异常处理）</summary>
        private IPrinterConnection EnsureConnection()
        {
            IPrinterConnection? conn = _connection;
            if (conn == null || !conn.IsConnected)
            {
                throw new InvalidOperationException("喷码机连接不可用。");
            }
            return conn;
        }

        // ============================================================
        // 接收线程（唯一读流入口，防粘包分流）
        // ============================================================

        /// <summary>
        /// 接收循环：持续读字节 → 灌缓冲区 → 按帧特征拆分。
        /// 【读超时 15 毫秒】取值理由见 RECEIVE_POLL_TIMEOUT_MS 常量说明：
        ///   决定无数据时多久醒来一次，影响取消响应速度与空转频率。
        /// 【锁竞争已消除】连接层的 Socket.Poll 等待段已移出连接锁（2026-09-10 18:35），
        ///   等待期间不持锁，因此读方不再顶住写方（发码 / 心跳）；本方法只负责把读到的
        ///   字节灌进缓冲区并分流，不参与锁竞争。
        /// </summary>
        private void ReceiveLoop()
        {
            CancellationToken token = _cts == null ? CancellationToken.None : _cts.Token;
            byte[] readBuffer = new byte[1024];

            while (!token.IsCancellationRequested)
            {
                IPrinterConnection? conn = _connection;

                if (conn == null)
                {
                    // [2026-09-10] 硬编码等待统一降到 50ms：产线速度快，等待越短响应越及时
                    Thread.Sleep(50);
                    continue;
                }

                int n;
                try
                {
                    // [2026-09-10] 读超时 200 → 50 → 15ms：
                    //   15ms 决定"无数据时多久醒一次"，取消响应更快（停止流程 Join 接收线程最坏等约 15.6ms）。
                    //   连接层 Poll 已移出锁，写方（发码 / 心跳）不再受此值影响。
                    //   具体取值理由见 RECEIVE_POLL_TIMEOUT_MS 常量说明。
                    n = conn.Read(readBuffer, RECEIVE_POLL_TIMEOUT_MS);
                }
                catch (ObjectDisposedException)
                {
                    // [2026-09-10] P1 竞态修复：连接被 Dispose 不只发生在停止流程 ——
                    //   断线重连的 SafeCloseConnection 同样会关旧连接，可能正好撞上本线程
                    //   阻塞在旧连接的 Read 中。此时绝不能退出线程（退出后重连成功也无人读流，
                    //   ACK/状态帧/0x32 全收不到 → 假在线 → 心跳超时 → 无限重连死循环）。
                    //   只有停止流程（取消信号已置位）才允许退出；
                    //   断线场景休眠后重试，等重连线程换上新连接自然恢复读流。
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }
                    Thread.Sleep(50);   // [2026-09-10] 硬编码等待统一降到 50ms
                    continue;   // 本次未读到数据（Read 未返回）：直接进入下一轮，不能落到 n 的判定
                }
                catch (Exception ex)
                {
                    // 读异常（串口拔线 / TCP 复位等）：记日志，稍后重试；
                    // 真断线由心跳超时统一判定，这里不做状态切换避免双重触发
                    LogWarn("【接收】读取异常：" + ex.Message);
                    Thread.Sleep(50);   // [2026-09-10] 硬编码等待统一降到 50ms
                    continue;
                }

                if (n <= 0)
                {
                    continue;
                }

                // [2026-09-10] 万总 19:53 指令：读到数据即刷新"最后交互时间"（心跳据此跳过探活）。
                //   只认读成功、不认写成功 —— 见 _lastIoMs 字段说明。
                MarkIoActivity();

                // [2026-09-12] 收到机器任何字节 = 对端活着：清零"连续应答超时"计数
                //   （心跳已停用，断线判定改由发码应答超时承担 —— 见 _ackTimeoutStreak 字段说明）
                System.Threading.Interlocked.Exchange(ref _ackTimeoutStreak, 0);

                lock (_rxLock)
                {
                    for (int i = 0; i < n; i++)
                    {
                        _rxBuffer.Add(readBuffer[i]);
                    }
                    ProcessRxBuffer();
                }
            }
        }

        /// <summary>
        /// 解析接收缓冲区（必须在 _rxLock 内调用）。
        /// 【分流规则（万总强调：查询指令绝不与打印指令混淆）】
        ///   0x06 / 0x15 → ACK 流（投递给等待中的事务 + 日志）
        ///   0x32        → 打印完成事件流（缓存计数-1 + 唤醒发码线程）
        ///   0x30        → 来源未明的字节（万总 2026-09-12：非打印完成信号，先静默忽略，不进 UI 不落盘）
        ///   1B 31 43…04 → 状态帧（心跳应答）
        ///   1B 54 31…04 → 计数帧（备用对账，仅日志）
        ///   其他        → 十六进制原样写日志，绝不参与业务判定
        /// </summary>
        private void ProcessRxBuffer()
        {
            while (_rxBuffer.Count > 0)
            {
                byte first = _rxBuffer[0];

                // ---------- 单字节事件 ----------
                if (first == CodeNetProtocol.ACK)
                {
                    _rxBuffer.RemoveAt(0);
                    DeliverAck(true);
                    continue;
                }

                if (first == CodeNetProtocol.NAK)
                {
                    _rxBuffer.RemoveAt(0);
                    DeliverAck(false);
                    // [2026-09-12] 万总指令：0x15 UI 完全静默，落盘按 30 秒汇总限频（见 OnNakReceived）
                    OnNakReceived();
                    continue;
                }

                if (first == CodeNetProtocol.PRINT_DONE)
                {
                    _rxBuffer.RemoveAt(0);
                    OnPrintDone();
                    continue;
                }

                // [2026-09-12] 万总指令：0x30 到达即静默忽略（不进 UI、不落盘）。
                // 【0x30 是什么——暂无定论】万总 2026-09-12 明确：0x30 不是打印完成信号，
                //   来源不明，先按未知字节静默丢弃，此处仅备注存疑线索、不作结论：
                //   SET_ACK（打印信号设置 1B 49 31 32 04 的字段 B）可设定"打印确认字符"
                //   （合法值 1-4 / A-Z），本程序设 '2'（0x32），模拟器疑回 '0'（0x30），
                //   未经现场确认、仅供参考。
                // 【行为边界】喷印计数只认 0x32，0x30 直接丢弃；探活不受影响
                //   （ReceiveLoop 收到任何字节已清零连续超时计数）。
                // 【帧安全】本分支只在 0x30 位于缓冲区头部时命中；状态帧/计数帧内容中的
                //   0x30（如 SSS="000"、HHMM 时间位）处于 0x1B 帧收集路径内，不会被误摘。
                if (first == 0x30)
                {
                    _rxBuffer.RemoveAt(0);
                    continue;
                }

                // ---------- 完整帧 ----------
                if (first == CodeNetProtocol.ESC)
                {
                    bool statusHead = _rxBuffer.Count >= 3 && _rxBuffer[1] == 0x31 && _rxBuffer[2] == 0x43;
                    bool countHead = _rxBuffer.Count >= 3 && _rxBuffer[1] == 0x54 && _rxBuffer[2] == 0x31;

                    if (statusHead)
                    {
                        if (_rxBuffer.Count < CodeNetProtocol.STATUS_FRAME_LENGTH)
                        {
                            break;  // 帧未收全，等下一个读周期
                        }

                        int statusCode;
                        string frameText;
                        if (CodeNetProtocol.TryParseStatusFrame(_rxBuffer, out statusCode, out frameText)
                            && _rxBuffer[11] == CodeNetProtocol.EOT)
                        {
                            _rxBuffer.RemoveRange(0, CodeNetProtocol.STATUS_FRAME_LENGTH);
                            OnStatusFrame(statusCode, frameText);
                            continue;
                        }

                        // 头部对但内容不合规：按垃圾帧丢弃整段，原样入日志
                        DumpUnknownFrame(CodeNetProtocol.STATUS_FRAME_LENGTH);
                        continue;
                    }

                    if (countHead)
                    {
                        if (_rxBuffer.Count < CodeNetProtocol.COUNT_FRAME_LENGTH)
                        {
                            break;
                        }

                        long count;
                        if (CodeNetProtocol.TryParseCountFrame(_rxBuffer, out count)
                            && _rxBuffer[13] == CodeNetProtocol.EOT)
                        {
                            _rxBuffer.RemoveRange(0, CodeNetProtocol.COUNT_FRAME_LENGTH);
                            LogDevice("【计数帧】喷头累计打印 " + count.ToString() + " 个（备用对账）");
                            continue;
                        }

                        DumpUnknownFrame(CodeNetProtocol.COUNT_FRAME_LENGTH);
                        continue;
                    }

                    // 0x1B 开头但不是认识的帧：丢弃首字节记日志（剩余字节下轮继续判，
                    // 这样即使喷码机发来未知长帧，也不会卡死缓冲区）
                    _rxBuffer.RemoveAt(0);
                    LogInfo("【接收】未知帧头字节 0x" + CodeNetProtocol.ToHexByte(first) + "（已忽略）");
                    continue;
                }

                // ---------- 完全无法识别的字节 ----------
                _rxBuffer.RemoveAt(0);
                LogInfo("【接收】未知字节 0x" + CodeNetProtocol.ToHexByte(first) + "（已忽略，不参与判定）");
            }

            // 兜底防膨胀：缓冲区异常膨胀说明对端在发垃圾流，整体丢弃防止内存无限增长
            if (_rxBuffer.Count > 4096)
            {
                byte[] garbage = _rxBuffer.ToArray();
                _rxBuffer.Clear();
                LogWarn("【接收】缓冲区超限（" + garbage.Length.ToString()
                        + " 字节），已整体丢弃。原始数据：0x" + CodeNetProtocol.ToHexString(garbage, 64));
            }
        }

        /// <summary>把缓冲区头部 count 个字节作为垃圾帧整体丢弃并入日志</summary>
        private void DumpUnknownFrame(int count)
        {
            if (count > _rxBuffer.Count)
            {
                count = _rxBuffer.Count;
            }

            byte[] garbage = new byte[count];
            for (int i = 0; i < count; i++)
            {
                garbage[i] = _rxBuffer[i];
            }
            _rxBuffer.RemoveRange(0, count);

            LogWarn("【接收】不合规帧已丢弃（" + count.ToString() + " 字节）：0x" + CodeNetProtocol.ToHexString(garbage, 64));
        }

        /// <summary>ACK 投递：唤醒正在等待应答的事务（无等待者时仅记日志）</summary>
        private void DeliverAck(bool acked)
        {
            lock (_ackLock)
            {
                if (_pendingAck != null)
                {
                    // [2026-09-10] 方案 1：先置"已收到"、再置结果值 —— 等待方以 Received 为唯一判据，
                    //   同一把 _ackLock 内赋值，保证它看到 Received=true 时 Acked 必已是最终值。
                    _pendingAck.Received = true;
                    _pendingAck.Acked = acked;
                }
                Monitor.PulseAll(_ackLock);
            }

            // 应答本身也写一条运行日志，方便现场对账（等待中的事务自己会再写结果）
            // [2026-09-10] 万总 19:53 指令：只进 UI 不落盘（属"给喷码机发码的反馈"）
            if (acked)
            {
                LogDevice("【应答】0x06 接收确认");
            }
        }

        /// <summary>
        /// 打印完成事件（0x32）：看板「喷印数」+1。
        /// [2026-09-10] 万总 20:23 指令：看板「喷印数」的唯一累加点就在这里 —— 只认 0x32。
        /// [2026-09-12] 万总指令：0x32 照常接收、照常显示、照常计数，但**不再驱动发码**
        ///   （发码已改为每 200ms 定时发一条，见 SendLoop；原配额 AddPendingSend 已删除）。
        /// </summary>
        private void OnPrintDone()
        {
            // 看板「喷印数」+1（本次运行累计，Start 时归零；口径见 _runPrintedCount 字段说明）
            System.Threading.Interlocked.Increment(ref _runPrintedCount);

            LogDevice("【打印完成】喷码机回传 0x32，本次运行已喷 " + RunPrintedCount.ToString() + " 条");

            // [2026-09-12] 发码已改定时驱动，当前无等待者；信号保留（恢复事件驱动时直接可用）
            _printDoneEvent.Set();
        }

        /// <summary>状态帧（心跳应答）：点亮状态帧信号 + 状态码变化时记日志</summary>
        private void OnStatusFrame(int statusCode, string frameText)
        {
            _statusFrameEvent.Set();

            // SSS 状态码：000 = 就绪正常；1xx 警告；2xx 打印禁止；100 故障（PDF 4.5）
            if (statusCode != _lastLoggedStatusCode)
            {
                _lastLoggedStatusCode = statusCode;

                if (statusCode == 0)
                {
                    LogDevice("【心跳】喷码机状态 " + statusCode.ToString("D3") + "（就绪正常）");
                }
                else
                {
                    LogWarn("【心跳】喷码机状态 " + statusCode.ToString("D3")
                            + "（非正常：1xx 警告 / 2xx 打印禁止 / 100 故障），帧：" + frameText);
                }
            }
        }

        // ============================================================
        // 心跳线程
        // ============================================================

        /// <summary>
        /// 心跳循环：周期发「查询机器状态」指令，等状态帧；超时判断线并触发重连。
        /// 【在线判定】收到状态帧 = 在线；HeartbeatTimeoutMs 内无状态帧 = 断线（万总设计目的）。
        /// 【配置即时生效】间隔/超时每轮从 ConfigHelper 现读，系统参数页保存后下轮生效。
        /// </summary>
        private void HeartbeatLoop()
        {
            CancellationToken token = _cts == null ? CancellationToken.None : _cts.Token;

            while (!token.IsCancellationRequested)
            {
                // ---------- 分片睡眠，响应取消 + 配置即时生效 ----------
                // [2026-09-10] 分片粒度统一降到 50ms：取消响应更快，心跳周期变更生效更及时
                int interval = ConfigHelper.HeartbeatIntervalMs;
                int slept = 0;
                while (slept < interval && !token.IsCancellationRequested)
                {
                    Thread.Sleep(50);
                    slept += 50;

                    // [2026-09-10] 万总 20:15 指令：边睡边看空闲 —— 一旦距离"最后一次读成功"
                    //   已满一个心跳间隔，立即结束本轮睡眠去探活，不再回炉再睡一整轮。
                    //   原因：原先"睡满整轮才判空闲"存在相位损耗 —— 若最后一条数据恰好在睡眠窗口
                    //         末尾才读到（空闲刚归零），到点判定必然"不满间隔"而跳过，白等一整轮，
                    //         断线发现最坏被拉到 2×间隔 + 超时（约 8 秒）。此处把最坏压回设计下限
                    //         "间隔 + 超时"（约 5 秒）。
                    //   产线连续读数据时 idle 始终 < 间隔，仍然一路睡满并跳过探活，锁收益不变。
                    long lastIoNow = System.Threading.Interlocked.CompareExchange(ref _lastIoMs, 0, 0);
                    if (Environment.TickCount64 - lastIoNow >= interval)
                    {
                        break;
                    }
                }

                if (token.IsCancellationRequested)
                {
                    break;
                }

                if (!IsRunning || !_printerOnline)
                {
                    continue;   // 断线期间由重连线程负责，心跳不动作
                }

                // [2026-09-10] 万总 19:53 指令：空闲跳过 —— 最近有成功读到数据（说明连接必然活着、
                //   且刚有交互）时本轮不发查询帧；只有"距最后一次读成功 ≥ 心跳间隔"才真正探活。
                //   产线连续生产时读一直有数据 → 心跳基本不执行 → 减少读写锁冲突（万总设计目的）。
                //   设备半死（TCP 挂着但不响应）场景：读不到数据 → _lastIoMs 不再更新 → 空闲超时后
                //   心跳介入 → 收不到状态帧 → 判定断线（这正是"只认读、不认写"的关键价值）。
                long lastIo = System.Threading.Interlocked.CompareExchange(ref _lastIoMs, 0, 0);
                long idleMs = Environment.TickCount64 - lastIo;

                if (idleMs < interval)
                {
                    continue;
                }

                try
                {
                    // 复位信号 → 写查询帧 → 等状态帧
                    _statusFrameEvent.Reset();

                    // [2026-09-10] 万总 19:53 指令：心跳正常态也显示在 UI（能看到系统在运行）
                    LogDevice("【心跳】发送查询状态指令（空闲探活）。");

                    lock (_ioLock)
                    {
                        EnsureConnection().Write(CodeNetProtocol.BuildQueryStatusFrame());
                    }

                    bool got = _statusFrameEvent.WaitOne(ConfigHelper.HeartbeatTimeoutMs < 1 ? 1 : ConfigHelper.HeartbeatTimeoutMs);

                    if (!got)
                    {
                        // [2026-09-10] B2 修复：超时后必须先确认服务仍在运行 ——
                        //   停止流程 Cancel 后本线程可能恰好从 WaitOne 醒来（等待窗口与 Cancel 存在竞争），
                        //   若不判断就会把已停止的服务刷成红色"断线"并拉起空转的重连线程。
                        if (token.IsCancellationRequested || !IsRunning)
                        {
                            break;
                        }

                        LogWarn("【心跳】" + ConfigHelper.HeartbeatTimeoutMs.ToString()
                                + " 毫秒内无状态帧，判定喷码机断线。");
                        TriggerReconnect();
                    }
                }
                catch (Exception ex)
                {
                    // 同上：停止过程中发生的写异常不得再触发断线重连
                    if (token.IsCancellationRequested || !IsRunning)
                    {
                        break;
                    }

                    // 写心跳失败 = 连接有问题，转断线重连
                    LogWarn("【心跳】发送异常：" + ex.Message);
                    TriggerReconnect();
                }
            }
        }

        // ============================================================
        // 发码线程（定时发码 v3：每 200ms 定时发一条，与 0x32 解耦）
        // ============================================================

        /// <summary>
        /// 发码循环：每 SEND_INTERVAL_MS（200ms）定时发一条 —— stash 非空重发 stash 码，
        /// 为空则取一条新码（取码即占用）。应答两分支判定：
        ///   0x06 = 机器确认收下 → 下轮取新码（stash 清空在 SendOneCode 内完成）；
        ///   0x15 / 应答超时 / 写失败 → 该码进 stash（在 SendOneCode 内完成），下轮重发同一个码。
        /// [2026-09-12] 万总指令：发码时机完全脱离 0x32 —— 0x32 只用于 UI 显示与「喷印数」计数。
        /// 【断线判定（心跳已停用）】应答超时连续 SEND_ACK_TIMEOUT_STREAK_MAX 次（默认 3）
        ///   → 判定断线 → TriggerReconnect；接收线程收到机器任何字节即清零计数（见 ReceiveLoop）。
        ///   按默认参数（超时 2000ms + 节拍 200ms）断线最坏约 3 × 2.2 ≈ 6.6 秒发现。
        /// 【写失败】码已进 stash，并立即转断线重连（连接层问题）；重连成功后 stash 保留、
        ///   排在预填 3 条之后等腾位自动补发（万总确认：顺序晚几拍不丢）。
        /// 【无码行为】stash 为空且库中无未喷码：本轮跳过等导入，只提示一次不刷屏。
        /// [2026-09-10] P3-2：与启动/重连的预填缓存互斥 —— 预填期间 IsRunning=false 且
        ///   _printerOnline=false，本线程在下方循环口即被挡住，不会并发取到同一条未喷码。
        ///   若日后改动预填/门闩的先后顺序，必须重新核对这里的两个判断条件。
        /// </summary>
        private void SendLoop()
        {
            CancellationToken token = _cts == null ? CancellationToken.None : _cts.Token;

            while (!token.IsCancellationRequested)
            {
                // [2026-09-12] 定时节拍：每 SEND_INTERVAL_MS 一轮（万总要求写成静态变量，见常量定义）
                Thread.Sleep(SEND_INTERVAL_MS);

                if (token.IsCancellationRequested || !IsRunning || !_printerOnline)
                {
                    continue;
                }

                // ---------- 1. 确定本条要发的码：stash 优先，无 stash 才查库取新码 ----------
                CodeData? codeToSend = null;
                bool isResend = false;

                if (_stashCode != null)
                {
                    // stash 非空：重发上一次未被确认收下的码（不重新取码、不重新占用、不重复计数）
                    codeToSend = _stashCode;
                    isResend = true;
                }
                else
                {
                    List<CodeData> codes = CodeDataDAL.GetNotPrintedCodes(1);

                    if (codes.Count == 0)
                    {
                        // 库中无码：本轮跳过等导入；只提示一次不刷屏
                        if (!_noCodeLogged)
                        {
                            _noCodeLogged = true;
                            LogWarn("【发码】库中没有未喷码，每 " + SEND_INTERVAL_MS.ToString()
                                    + " 毫秒自动重试，等待导入。");
                        }
                        continue;
                    }

                    _noCodeLogged = false;
                    codeToSend = codes[0];
                }

                // ---------- 2. 发送（占用/计数/stash 收尾都在 SendOneCode 内完成） ----------
                SendResult result = SendOneCode(codeToSend, isResend);

                // ---------- 3. 结果分流：只处理需要 SendLoop 决策的三类 ----------
                if (result == SendResult.AckTimeout)
                {
                    // 连续应答超时计数（接收线程收到任何字节会清零）；
                    // 达到阈值 = "写进去却连续无反馈" → 判定断线（心跳停用后的唯一断线来源）
                    int streak = System.Threading.Interlocked.Increment(ref _ackTimeoutStreak);

                    if (streak >= SEND_ACK_TIMEOUT_STREAK_MAX)
                    {
                        LogWarn("【发码】连续 " + streak.ToString() + " 次发码应答超时，判定喷码机断线。");
                        System.Threading.Interlocked.Exchange(ref _ackTimeoutStreak, 0);
                        TriggerReconnect();
                    }
                }
                else if (result == SendResult.WriteFailed)
                {
                    // 写失败：连接有问题，立即转断线重连（码已进 stash，重连后自动补发）
                    LogWarn("【发码】写入失败，转断线重连流程。");
                    TriggerReconnect();
                }
                else if (result == SendResult.ClaimFailed)
                {
                    // 占用失败（库异常 / 码被占）：码未被占用仍在库中，下轮重取自然重试；
                    // 连接没毛病，不重连
                    LogWarn("【发码】取码占用失败，本轮跳过（不重连，下轮重试）。");
                }
                // Acked / Nacked：无需额外处理 —— stash 的清/留在 SendOneCode 内已收尾；
                // 0x15 的日志由接收线程 OnNakReceived 限频汇总
            }
        }

        // ============================================================
        // 断线重连
        // ============================================================

        /// <summary>
        /// 触发断线重连（心跳超时 / 发码写入失败时调用）。
        /// 【互斥】_reconnectFlag 防止多处同时拉起重连线程。
        /// </summary>
        private void TriggerReconnect()
        {
            // [2026-09-10] B2 修复：入口拦一道 —— 服务已停止 / 正在停止 / 取消信号已置位时，
            //   一律不允许再拉起重连线程（停止瞬间残留的心跳线程可能刚好走到这里）。
            if (!IsRunning)
            {
                return;
            }

            CancellationTokenSource? cts = _cts;
            if (cts == null || cts.IsCancellationRequested)
            {
                return;
            }

            if (System.Threading.Interlocked.CompareExchange(ref _reconnectFlag, 1, 0) != 0)
            {
                return; // 已有重连在跑
            }

            _printerOnline = false;
            FirePrinterState("断线", System.Drawing.Color.Red);

            try
            {
                SafeCloseConnection("断线处理");
            }
            catch
            {
                // 关闭失败不影响重连流程
            }

            _reconnectThread = new Thread(ReconnectLoop);
            _reconnectThread.IsBackground = true;
            _reconnectThread.Name = "PrintService-Reconnect";
            _reconnectThread.Start();
        }

        /// <summary>
        /// 重连循环：按配置间隔反复尝试，成功后重新初始化协议环境并补足缓存。
        /// 【重算依据】断线后机内队列内容已不可信（可能打印了部分/被复位清掉），
        ///   缓存计数清零；断线期间缓存里的码按 v5 写入制保持已喷、绝不重发。
        /// </summary>
        private void ReconnectLoop()
        {
            CancellationToken token = _cts == null ? CancellationToken.None : _cts.Token;

            try
            {
                while (!token.IsCancellationRequested && IsRunning)
                {
                    int interval = ConfigHelper.ReconnectIntervalMs;
                    LogInfo("【重连】" + interval.ToString() + " 毫秒后尝试重连……");

                    // 分片睡眠
                    // [2026-09-10] 分片粒度统一降到 50ms（与心跳一致），取消响应更快
                    int slept = 0;
                    while (slept < interval && !token.IsCancellationRequested && IsRunning)
                    {
                        Thread.Sleep(50);
                        slept += 50;
                    }

                    if (token.IsCancellationRequested || !IsRunning)
                    {
                        break;
                    }

                    try
                    {
                        // 1. 按最新配置建连接（现场可能中途改了配置）
                        PrinterConfigBLL.LoadResult load = PrinterConfigBLL.Load();
                        if (!load.Success)
                        {
                            throw new InvalidOperationException(load.Message);
                        }

                        IPrinterConnection conn = CreateConnection(load);
                        conn.Open();
                        _connection = conn;

                        // 2. 重新初始化协议环境（连接是新的，信号设置必须重发）
                        ExecInstruction(CodeNetProtocol.BuildSignalSetupFrame(), "重连后打印信号设置");
                        ExecInstruction(CodeNetProtocol.BuildClearQueueFrame(0), "重连后清 TCP/IP 队列");
                        ExecInstruction(CodeNetProtocol.BuildClearQueueFrame(1), "重连后清 RS232 队列");
                        ExecInstruction(CodeNetProtocol.BuildClearQueueFrame(2), "重连后清历史队列");

                        // 3. 连续超时计数清零 + 丢弃旧接收缓冲
                        // [2026-09-12] 定时发码 v3：原配额 ResetPendingSend 已删除；
                        //   stash 按万总指令**保留** —— stash 码在重连后排在预填 3 条之后等腾位，
                        //   由发码线程每 200ms 重发，顺序晚几拍但不会丢（万总确认）。
                        System.Threading.Interlocked.Exchange(ref _ackTimeoutStreak, 0);
                        lock (_rxLock)
                        {
                            _rxBuffer.Clear();
                        }

                        // 4. 补足缓存（占用制：逐条取码即标已喷）
                        // [2026-09-12] 定时发码 v3：0x15/超时/写失败 → 该码已进 stash，预填提前收尾
                        //   （运行态每 200ms 自动重发），重连继续；占用失败仍中止重连（防重优先）
                        List<CodeData> codes = CodeDataDAL.GetNotPrintedCodes(_targetCache);
                        int prefilled = 0;
                        for (int i = 0; i < codes.Count; i++)
                        {
                            SendResult result = SendOneCode(codes[i], false);

                            if (result == SendResult.ClaimFailed)
                            {
                                throw new InvalidOperationException("重连后预填缓存失败（取码占用失败）。");
                            }

                            if (result != SendResult.Acked)
                            {
                                LogWarn("【重连】预填第 " + (i + 1).ToString()
                                        + " 条未获喷码机确认（0x15/超时/写失败），该码已转 stash 待自动重发，剩余预填取消。");
                                break;
                            }

                            prefilled++;
                        }

                        // 5. 回到在线
                        // [2026-09-10] 防御（万总 15:11 批复）：预填耗时期间用户可能已点「结束喷码」
                        //   —— 停止流程 Join 重连线程超时后会先走完七步（连接由本线程兜底清理），
                        //   此时绝不能再把状态/UI 拉回"运行中"，关掉刚建的连接直接退出。
                        if (token.IsCancellationRequested || !IsRunning)
                        {
                            LogInfo("【重连】重连完成时服务已停止，关闭本次连接并退出。");
                            SafeCloseConnection("停止期间重连完成清理");
                            break;
                        }

                        _printerOnline = true;
                        FirePrinterState("运行中", System.Drawing.Color.Green);
                        LogInfo("【重连】重连成功，已预填 " + prefilled.ToString()
                                + " 条，恢复每 " + SEND_INTERVAL_MS.ToString()
                                + " 毫秒定时发码（stash 保留待重发）。");
                        break;
                    }
                    catch (Exception ex)
                    {
                        LogWarn("【重连】失败：" + ex.Message);
                        SafeCloseConnection("重连失败清理");
                    }
                }
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _reconnectFlag, 0);
            }
        }

        // ============================================================
        // 测试喷印
        // ============================================================

        /// <summary>
        /// 测试喷印序列（独立线程执行）：新建独立连接 → 打印信号设置 → 清三条队列 →
        ///   发固定值 AB12345 → 关闭并释放连接。
        /// 【完全独立】不复用生产连接、不占生产码 Id、不写数据库、不参与补码配额；
        ///   测试时生产必已停止（互斥保证），故清三条队列不影响任何生产在途数据。
        /// 【释放铁律（万总 19:53 三次强调）】无论成功 / 拒收 / 超时 / 异常，
        ///   finally 一定 Close() + Dispose() 并置空引用，绝不留下未关闭的连接对象。
        /// </summary>
        private void TestPrintSequence()
        {
            // 连接对象在 try 之外声明并初始化为 null：即使 Open 抛异常也能在 finally 里判空释放
            IPrinterConnection? testConn = null;

            try
            {
                // ---------- 1. 读配置并新建独立连接 ----------
                PrinterConfigBLL.LoadResult load = PrinterConfigBLL.Load();
                if (!load.Success)
                {
                    LogDevice("【测试喷印】读取喷码机配置失败：" + load.Message);
                    return;
                }

                testConn = CreateConnection(load);
                _testConnection = testConn;      // 供停止流程 / 程序退出兜底关闭
                testConn.Open();
                LogDevice("【测试喷印】已建立独立连接（" + DescribeConnection(load) + "）。");

                // ---------- 2. 打印信号设置 + 清三条缓存队列（与生产启动同一组指令） ----------
                WaitAckDirect(testConn, CodeNetProtocol.BuildSignalSetupFrame(), "打印信号设置");
                WaitAckDirect(testConn, CodeNetProtocol.BuildClearQueueFrame(0), "清 TCP/IP 缓存队列");
                WaitAckDirect(testConn, CodeNetProtocol.BuildClearQueueFrame(1), "清 RS232 缓存队列");
                WaitAckDirect(testConn, CodeNetProtocol.BuildClearQueueFrame(2), "清历史缓存队列");

                // ---------- 3. 发固定值 AB12345，等 0x06 应答 ----------
                bool? ack = WaitAckDirect(testConn, CodeNetProtocol.BuildCacheDataFrame("AB12345"), "测试码 AB12345");

                if (ack == true)
                {
                    LogDevice("【测试喷印】AB12345 已被喷码机接收（0x06），请观察喷头喷印效果。");
                }
                else if (ack == false)
                {
                    LogDevice("【测试喷印】AB12345 被喷码机拒收（0x15）：请检查机内动态文本模板是否已设置！");
                }
                else
                {
                    LogDevice("【测试喷印】AB12345 发送后应答超时，请观察喷印效果。");
                }
            }
            catch (Exception ex)
            {
                // 建连失败 / 写入失败等：只进 UI，方便操作员当场看到（不落盘）
                LogDevice("【测试喷印】执行失败：" + ex.Message);
            }
            finally
            {
                // ---------- 4. 无论成功失败，完全释放本次测试连接（万总铁律） ----------
                try
                {
                    IPrinterConnection? conn = testConn;
                    testConn = null;

                    if (conn != null)
                    {
                        conn.Close();       // 接口契约：重复调用安全、不抛异常
                        conn.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    // 释放异常绝不外抛（本块已在 finally 内，抛出去会盖掉前面的执行结果）
                    LogHelper.Instance.Error("测试喷印：关闭连接失败", ex);
                }
                _testConnection = null;

                // ---------- 5. 状态复位（仅当仍处于"测试中"才复位，避免与停止流程打架） ----------
                bool needReset;
                lock (_stateLock)
                {
                    needReset = (_state == ServiceState.Testing);
                    if (needReset)
                    {
                        _state = ServiceState.Stopped;
                    }
                }

                System.Threading.Interlocked.Exchange(ref _testFlag, 0);
                _testPrintThread = null;

                if (needReset)
                {
                    FireServiceState("未启动", false);
                }
            }
        }

        // ============================================================
        // 停止流程（七步固定顺序，任何一步失败都继续走完 —— 不许半途而废）
        // ============================================================

        /// <summary>停止流程重入标志：0=空闲 1=执行中（防 Stop 后台线程与 StopSync 并发双重执行）</summary>
        private int _stopFlag = 0;

        /// <summary>
        /// 停止流程入口（带重入保护）。
        /// [2026-09-10] 防御（万总 15:11 批复）：用户点「结束喷码」后立刻关闭窗体时，
        ///   Stop 拉起的后台线程与 FormClosed 触发的 StopSync 可能同时进入停止流程 ——
        ///   七步重复执行虽然多数步骤无害（Cancel/Join/关连接均有判空），但 CTS 双 Dispose、
        ///   日志双写等仍属脏并发。此处用 Interlocked 标志拒绝重入：后到者直接返回，
        ///   由先到者负责走完全程与 UI 复原。
        /// </summary>
        private void StopSteps(string reason)
        {
            if (System.Threading.Interlocked.CompareExchange(ref _stopFlag, 1, 0) != 0)
            {
                LogInfo("【停止】停止流程已在执行中（本次来自：" + reason + "），忽略重复调用。");
                return;
            }

            try
            {
                StopStepsCore(reason);
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _stopFlag, 0);
            }
        }

        /// <summary>
        /// 七步停止流程实际执行体（万总要求：多线程全部退出、各种对象完全释放）：
        ///   ① 停心跳（取消信号）→ ② 停接收/心跳/发码/重连线程（Join）→ ③ 清喷码机队列 →
        ///   ④ 不回退任何码（取码占用制铁律）→ ⑤ 关连接 → ⑥ 释放对象 → ⑦ UI 复原。
        /// </summary>
        private void StopStepsCore(string reason)
        {
            LogInfo("【停止】开始执行停止流程（" + reason + "）");

            // ---------- ① 停心跳（先停，杜绝停止过程中再发心跳） ----------
            try
            {
                if (_cts != null)
                {
                    _cts.Cancel();
                }
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Error("停止流程：取消信号失败", ex);
            }
            _printerOnline = false;

            // ---------- ② 停接收/心跳/发码/重连线程 ----------
            // [2026-09-10] B2 修复：必须包含心跳线程 —— 漏 Join 会让它在停止流程结束后才醒来，
            //   把 lblPrinterStatus 刷成红色"断线"（实际已停止，应显示"未连接"），
            //   并可能拉起一条空转的重连线程。
            JoinThread(_receiveThread, "接收线程");
            JoinThread(_heartbeatThread, "心跳线程");
            JoinThread(_sendThread, "发码线程");
            JoinThread(_reconnectThread, "重连线程");
            JoinThread(_testPrintThread, "测试喷印线程");
            _receiveThread = null;
            _heartbeatThread = null;
            _sendThread = null;
            _reconnectThread = null;
            _testPrintThread = null;

            // ---------- ③ 清喷码机队列（连接还活着才发，失败不影响后续步骤） ----------
            //   把机内残存未打数据清掉，让喷码机回到干净状态；
            //   残存数据对应的码已按 v5 写入制标为已喷，不再补发。
            try
            {
                IPrinterConnection? conn = _connection;
                if (conn != null && conn.IsConnected)
                {
                    lock (_ioLock)
                    {
                        conn.Write(CodeNetProtocol.BuildClearQueueFrame(0));
                        conn.Write(CodeNetProtocol.BuildClearQueueFrame(1));
                        conn.Write(CodeNetProtocol.BuildClearQueueFrame(2));
                    }
                    LogInfo("【停止】已发送清三条缓存队列指令（机内残存数据已清）。");
                }
            }
            catch (Exception ex)
            {
                LogWarn("【停止】清队列失败（连接可能已断，继续后续步骤）：" + ex.Message);
            }

            // ---------- ④ v5 写入制：不回退任何码 ----------
            //   已标已喷的码保持已喷（哪怕机内还没打出来），绝不回退、绝不重发，
            //   防止重复喷印（万总 14:20 铁律）。

            // ---------- ⑤ 关连接（含测试喷印的独立连接） ----------
            SafeCloseConnection(reason);
            CloseTestConnection(reason);

            // ---------- ⑥ 释放对象 ----------
            try
            {
                if (_cts != null)
                {
                    _cts.Dispose();
                    _cts = null;
                }
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Error("停止流程：释放取消信号失败", ex);
            }

            lock (_ackLock)
            {
                _pendingAck = null;
            }
            // [2026-09-12] 定时发码 v3：停止即清空 stash（万总裁定：停机烧 1 码没关系）；
            //   原配额 ResetPendingSend 已删除。
            //   【时序】此时发码线程已在 ② 被 Join（或超时放弃），stash 不会再被写回。
            _stashCode = null;
            lock (_rxLock)
            {
                _rxBuffer.Clear();
            }
            System.Threading.Interlocked.Exchange(ref _reconnectFlag, 0);
            System.Threading.Interlocked.Exchange(ref _testFlag, 0);

            // ---------- ⑦ UI 复原 ----------
            lock (_stateLock)
            {
                _state = ServiceState.Stopped;
            }
            FirePrinterState("未连接", System.Drawing.Color.FromArgb(0, 64, 0));
            FireServiceState("未启动", false);
            LogInfo("【停止】停止流程完成，服务回到未启动状态。");
        }

        /// <summary>等待线程退出（最多 3 秒），超时记日志放弃 —— 线程是后台线程，不会阻碍进程退出</summary>
        private void JoinThread(Thread? thread, string name)
        {
            try
            {
                if (thread != null && thread.IsAlive)
                {
                    if (!thread.Join(3000))
                    {
                        LogWarn("【停止】" + name + " 3 秒内未退出，放弃等待（后台线程不阻进程退出）。");
                    }
                }
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Error("停止流程：等待" + name + "退出失败", ex);
            }
        }

        /// <summary>安全关闭并释放连接（任何异常都吞掉，绝不影响调用方流程）</summary>
        private void SafeCloseConnection(string reason)
        {
            try
            {
                IPrinterConnection? conn = _connection;
                _connection = null;

                if (conn != null)
                {
                    conn.Close();
                    conn.Dispose();
                    LogInfo("【连接】已关闭（" + reason + "）。");
                }
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Error("关闭喷码机连接失败（已忽略继续）", ex);
            }
        }

        /// <summary>
        /// 安全关闭并释放测试喷印的独立连接（任何异常都吞掉，绝不影响调用方流程）。
        /// 【场景】程序退出 / 停止流程兜底 —— 测试线程若已自行释放则此处为空操作，重复调用安全。
        /// </summary>
        private void CloseTestConnection(string reason)
        {
            try
            {
                IPrinterConnection? conn = _testConnection;
                _testConnection = null;

                if (conn != null)
                {
                    conn.Close();
                    conn.Dispose();
                    LogHelper.Instance.Info("【测试喷印】独立连接已关闭（" + reason + "）。");
                }
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Error("关闭测试喷印连接失败（已忽略继续）", ex);
            }
        }

        // ============================================================
        // 日志与事件（LogHelper 落盘 + 事件给 UI）
        // ============================================================

        /// <summary>普通日志：落盘 + 推给 UI 运行日志区</summary>
        private void LogInfo(string message)
        {
            LogHelper.Instance.Info(message);
            FireRunLog(message);
        }

        /// <summary>警告日志：落盘（含 error 副本不写，warn 级别）+ 推给 UI</summary>
        private void LogWarn(string message)
        {
            LogHelper.Instance.Warn(message);
            FireRunLog(message);
        }

        /// <summary>错误日志：落盘（同时进 error 副本）+ 推给 UI</summary>
        private void LogError(string message)
        {
            LogHelper.Instance.Error(message);
            FireRunLog(message);
        }

        /// <summary>
        /// 设备交互日志：**只推 UI、绝不落盘**（万总 2026-09-10 19:53 指令）。
        /// 【用途】让操作员在运行日志区看懂"发码 → 0x06 接收 → 0x32 打印完成"的交互节奏：
        ///   覆盖 0x06 应答 / 0x32 打印完成 / 【发码】/ 指令成功 / 心跳收发 / 计数帧 / 测试喷印全部行。
        /// 【为什么不落盘】产线 1 秒 3 瓶时每瓶 3 行交互 → 一天十几万行，磁盘日志会被淹没，
        ///   真要倒查的读写异常反而找不到。落盘只留业务/状态机事件 + 读写异常。
        /// 【边界】消息为空时 FireRunLog 会推一条空行，这里直接跳过，避免 UI 出现无意义空行。
        /// </summary>
        private void LogDevice(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return;
            }

            FireRunLog(message);
        }

        private void FireRunLog(string message)
        {
            Action<string>? handler = RunLog;
            if (handler != null)
            {
                try
                {
                    handler(message);
                }
                catch (Exception ex)
                {
                    // UI 端异常绝不能反噬后台线程
                    System.Diagnostics.Debug.WriteLine("RunLog 事件处理异常：" + ex.Message);
                }
            }
        }

        private void FireServiceState(string text, bool isRunning)
        {
            Action<string, bool>? handler = ServiceStateChanged;
            if (handler != null)
            {
                try
                {
                    handler(text, isRunning);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("ServiceStateChanged 事件处理异常：" + ex.Message);
                }
            }
        }

        private void FirePrinterState(string text, System.Drawing.Color color)
        {
            Action<string, System.Drawing.Color>? handler = PrinterStateChanged;
            if (handler != null)
            {
                try
                {
                    handler(text, color);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("PrinterStateChanged 事件处理异常：" + ex.Message);
                }
            }
        }

        private void FireDashboard()
        {
            Action? handler = DashboardChanged;
            if (handler != null)
            {
                try
                {
                    handler();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("DashboardChanged 事件处理异常：" + ex.Message);
                }
            }
        }
    }
}
