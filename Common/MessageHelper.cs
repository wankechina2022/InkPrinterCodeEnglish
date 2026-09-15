namespace InkPrinterCode.Common
{
    /// <summary>
    /// [2026-09-10] 消息提示帮助类 —— 统一全窗体的弹框风格（标题、图标、按钮组合）
    ///
    /// 【为什么要统一】开发规约要求界面反馈一致：同类操作用同样的标题和图标，
    ///   避免有的地方标题写"提示"、有的写"系统提示"，也避免删除确认漏掉图标。
    ///
    /// 【使用约定】
    ///   - 删除 / 更新 / 保存 / 导入这类会改数据的操作，执行前必须先 ShowConfirm 让用户确认；
    ///   - 面向用户的文案只讲现象和处理建议，不要把异常堆栈甩给用户（堆栈进 error 日志）。
    ///
    /// 【边界】所有方法对 null 文案做兜底，按空串处理，避免 MessageBox 抛异常。
    /// </summary>
    public static class MessageHelper
    {
        /// <summary>信息提示</summary>
        public static void ShowInfo(string? message)
        {
            MessageBox.Show(SafeText(message), "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>警告提示（校验不通过、业务规则拦截等）</summary>
        public static void ShowWarning(string? message)
        {
            MessageBox.Show(SafeText(message), "警告", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        /// <summary>错误提示（操作失败）</summary>
        public static void ShowError(string? message)
        {
            MessageBox.Show(SafeText(message), "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        /// <summary>
        /// 确认对话框
        /// </summary>
        /// <param name="message">确认文案，建议写清"要做什么、影响多少条数据"</param>
        /// <returns>用户点「是」返回 true，其余返回 false</returns>
        public static bool ShowConfirm(string? message)
        {
            DialogResult result = MessageBox.Show(SafeText(message), "确认",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            return result == DialogResult.Yes;
        }

        /// <summary>
        /// 危险操作确认对话框 —— 默认焦点在「否」上，防手快连按回车误删
        /// </summary>
        /// <param name="message">确认文案</param>
        /// <returns>用户点「是」返回 true，其余返回 false</returns>
        public static bool ShowDangerConfirm(string? message)
        {
            DialogResult result = MessageBox.Show(SafeText(message), "危险操作确认",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            return result == DialogResult.Yes;
        }

        /// <summary>操作成功提示，例如 ShowSuccess("导入") 显示"导入成功！"</summary>
        public static void ShowSuccess(string? operation = "操作")
        {
            ShowInfo(SafeText(operation) + "成功！");
        }

        /// <summary>操作失败提示</summary>
        public static void ShowFail(string? operation = "操作")
        {
            ShowError(SafeText(operation) + "失败，请查看 Logs 目录下的 error 日志或联系管理员！");
        }

        /// <summary>
        /// [2026-09-10] 喷码服务运行中提示（数据查看类操作共用：打开窗体 / 查询 / 导出Excel）
        /// 【为什么只放文案、不判状态】本类在 Common 层，层依赖为 UI → BLL → DAL → Model，
        ///   Common 被各层引用但不能反向引用 BLL，故这里不持有 PrintServiceBLL，
        ///   由调用方用自己的服务实例判 IsRunning 之后再调本方法。
        /// 【性质】提示但不拦截：用户点掉「确定」后照常继续。
        /// </summary>
        public static void ShowRunningServiceTip()
        {
            ShowWarning("喷码服务正在运行中。\r\n\r\n"
                + "本页面会读取数据库，与发码取码可能短暂争用，使发码节奏略有变慢"
                + "（不影响已发码的正确性）。\r\n\r\n"
                + "点「确定」后继续。");
        }

        /// <summary>
        /// [2026-09-10] 喷码服务运行中提示（数据导入专用 —— 后果比查询重得多，文案单独加重）
        /// 【与 ShowRunningServiceTip 的区别】导入是"整批数据单事务写库"，
        ///   会长时间占住数据库（SQLite 为整库文件锁），发码线程的取码/标已喷可能被阻塞，
        ///   后果不是"节奏略慢"而是"可能停顿"，所以提示级别更高、措辞更明确。
        /// 【性质】提示但不拦截：用户点掉「确定」后照常继续导入。
        /// </summary>
        public static void ShowRunningImportTip()
        {
            ShowWarning("喷码服务正在运行中！\r\n\r\n"
                + "导入需向数据库写入大量数据，期间会长时间占用数据库，"
                + "发码取码可能被阻塞、导致发码停顿。\r\n\r\n"
                + "点「确定」后继续导入。");
        }

        /// <summary>
        /// 文案兜底：null 转空串
        /// </summary>
        private static string SafeText(string? text)
        {
            if (text == null)
            {
                return string.Empty;
            }
            return text;
        }
    }
}
