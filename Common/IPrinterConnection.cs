namespace InkPrinterCode.Common
{
    /// <summary>
    /// [2026-09-10] 喷码机连接抽象接口（阶段二第 2 轮新增）
    ///
    /// 【设计目的（万总确认的结构）】
    ///   TCP 与串口各写一个实现类（TcpPrinterConnection / SerialPrinterConnection），
    ///   指令组装与返回值解析独立在 CodeNetProtocol —— 连接层只管"字节进出"，
    ///   不认识协议；协议层只管"组帧解帧"，不认识连接。两边互不掺杂。
    ///
    /// 【实现约定】
    ///   1. 实现类内部必须自带一把读写锁：Write 与 Read 可能被不同线程调用
    ///      （业务层 _ioLock 串行"发送→等应答"事务，接收线程独立读流），
    ///      连接层锁只保护流对象本身不被并发访问；
    ///   2. Open 失败必须抛异常（连接失败原因要能传到 UI），Close/Dispose 不得抛异常；
    ///   3. Read 返回本次实际读到的字节数：超时无数据返回 0，连接被对端关闭抛 IOException；
    ///   4. 所有实现类线程安全性只到"流对象"级别，事务级串行由 PrintServiceBLL 的 _ioLock 负责
    ///      （两级锁职责分开，见方案文档 3.1）。
    /// </summary>
    public interface IPrinterConnection : IDisposable
    {
        /// <summary>连接是否可用（心跳超时判定才是真正的在线依据，此属性只反映对象状态）</summary>
        bool IsConnected { get; }

        /// <summary>
        /// 建立连接（TCP：可选 700 预复位；串口：打开 COM 口）。
        /// 失败抛异常，由调用方（PrintServiceBLL）转成 UI 提示与日志。
        /// </summary>
        void Open();

        /// <summary>关闭连接并释放底层资源。重复调用安全，不得抛异常</summary>
        void Close();

        /// <summary>写出原始字节。失败抛异常（数据未出去 → 调用方按"写入异常"处理）</summary>
        void Write(byte[] data);

        /// <summary>
        /// 读取原始字节
        /// </summary>
        /// <param name="buffer">承接缓冲区</param>
        /// <param name="timeoutMs">等待数据的最长时间（毫秒）</param>
        /// <returns>实际读到的字节数（0 = 超时无数据）；连接异常时抛异常</returns>
        int Read(byte[] buffer, int timeoutMs);
    }
}
