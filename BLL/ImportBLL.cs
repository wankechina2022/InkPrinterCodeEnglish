using System.Diagnostics;
using Microsoft.Data.Sqlite;
using InkPrinterCode.Common;
using InkPrinterCode.DAL;
using InkPrinterCode.Model;

namespace InkPrinterCode.BLL
{
    /// <summary>
    /// [2026-09-10] 数据导入业务层 —— 读文件 → 校验 → 去重 → 单个事务入库 → 写台账
    ///
    /// 【全链路】
    ///   1. 文件存在性检查 + 扩展名分流（.txt / .xls / .xlsx，其他直接拒绝，绝不猜格式）
    ///   2. 读取源文件（txt 逐行 / Excel 第一列），空行保留占位以保证行号对得上
    ///   3. 逐行清洗（只 Trim）与校验：空行、含中文 → 计入无效并跳过
    ///   4. 文件内去重（HashSet，保留首次出现）
    ///   5. 库内去重（双路径，见下方说明）
    ///   6. 单事务写入：先插台账拿 BatchId，再批量插码，最后回写台账计数
    ///
    /// 【去重双路径】
    ///   库内总条数 &lt;= DedupHashSetThreshold（默认 100 万）→ 一次性把库里所有码读进内存 HashSet 比对（最快）；
    ///   超过阈值 → 只在候选码范围内做分批 IN 查询（500 个参数一批），牺牲一点速度换内存安全。
    ///   两条路径结果完全一致，区别只在内存占用。
    ///
    /// 【取消与回滚】
    ///   读取阶段：token 直接传给 TxtHelper / ExcelHelper，取消立即抛 OperationCanceledException；
    ///   写库阶段：每批开始前检查 token，一旦取消就中止插入并抛异常，让事务整体回滚 ——
    ///             绝不允许出现"导了一半"的数据（半截数据比不导更麻烦，现场无法判断哪些没导进来）。
    ///
    /// 【边界】
    ///   即使 0 条有效（比如全是重复码），台账也照写，方便追溯"这个文件导过、全被判重了"。
    /// </summary>
    public static class ImportBLL
    {
        /// <summary>
        /// 执行导入
        /// </summary>
        /// <param name="filePath">源文件完整路径</param>
        /// <param name="token">取消信号，由调用方（进度窗体）持有并触发</param>
        /// <param name="reportProgress">进度回调：参数1 = 百分比(0~100)，参数2 = 当前阶段说明文字</param>
        /// <returns>导入结果（成功 / 失败 / 被取消 三态见 ImportResult 注释）</returns>
        public static ImportResult ImportFromFile(string filePath, CancellationToken token, Action<int, string>? reportProgress)
        {
            ImportResult result = new ImportResult();
            Stopwatch watch = new Stopwatch();
            watch.Start();

            // ---------- 1. 前置检查 ----------
            if (!ValidateHelper.IsFileExists(filePath))
            {
                result.Success = false;
                result.Message = "文件不存在或无法访问：" + filePath;
                LogHelper.Instance.Warn("导入被拒绝，文件不存在：" + filePath);
                return result;
            }

            bool supported = false;
            ImportSourceType sourceType = EnumHelper.GetSourceTypeByExtension(filePath, out supported);
            if (!supported)
            {
                result.Success = false;
                result.Message = "不支持的文件格式，仅支持 .txt / .xls / .xlsx";
                LogHelper.Instance.Warn("导入被拒绝，不支持的格式：" + filePath);
                return result;
            }

            string fileName = Path.GetFileName(filePath);
            LogHelper.Instance.Info("开始导入 文件=" + fileName + " 类型=" + EnumHelper.GetSourceTypeText(sourceType) + " 路径=" + filePath);

            // ---------- 2. 读取源文件 ----------
            List<string> rawLines = new List<string>();
            int totalRows = 0;

            try
            {
                Report(reportProgress, 5, "正在读取文件...");

                if (sourceType == ImportSourceType.Txt)
                {
                    totalRows = TxtHelper.ReadAllLines(filePath, rawLines, token);
                }
                else
                {
                    totalRows = ExcelHelper.ReadFirstColumn(filePath, rawLines, token);
                }
            }
            catch (OperationCanceledException)
            {
                result.Success = false;
                result.Canceled = true;
                result.Message = "已取消导入，未写入任何数据";
                LogHelper.Instance.Info("导入在读取阶段被取消：" + fileName);
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = "读取文件失败：" + ex.Message;
                LogHelper.Instance.Error("读取导入文件失败：" + filePath, ex);
                return result;
            }

            result.TotalRows = totalRows;
            Report(reportProgress, 20, "文件读取完成，共 " + totalRows.ToString() + " 行，正在校验...");

            if (totalRows <= 0)
            {
                result.Success = false;
                result.Message = "文件没有任何内容，未导入";
                LogHelper.Instance.Warn("导入文件为空：" + filePath);
                return result;
            }

            // ---------- 3. 清洗 + 校验 + 文件内去重 ----------
            LogHelper.Instance.BeginDuplicateSession(ConfigHelper.DuplicateLogLimit);

            List<CodeData> candidates = new List<CodeData>();
            HashSet<string> fileSet = new HashSet<string>();

            int progressStep = ConfigHelper.ProgressReportStep;
            if (progressStep <= 0)
            {
                progressStep = 1000;
            }

            for (int i = 0; i < rawLines.Count; i++)
            {
                int rowNo = i + 1;
                string codeValue = ValidateHelper.NormalizeCode(rawLines[i]);

                string invalidReason = string.Empty;
                if (!ValidateHelper.IsValidCode(codeValue, out invalidReason))
                {
                    result.InvalidCount++;
                    if (invalidReason == "空行")
                    {
                        result.EmptyRowCount++;
                    }
                    else
                    {
                        result.ChineseRowCount++;
                    }
                    continue;
                }

                // 文件内重复：保留首次出现的那条，后续的同码计入重复
                if (!fileSet.Add(codeValue))
                {
                    result.FileDuplicateCount++;
                    LogHelper.Instance.LogDuplicate(codeValue, rowNo, "文件内重复");
                    continue;
                }

                CodeData item = new CodeData();
                item.RowNo = rowNo;
                item.CodeValue = codeValue;
                item.PrintStatus = PrintStatus.NotPrinted;
                item.CreateTime = Extensions.NowDbTimeString();
                candidates.Add(item);

                if (candidates.Count % progressStep == 0)
                {
                    if (token.IsCancellationRequested)
                    {
                        LogHelper.Instance.EndDuplicateSession("导入在校验阶段被取消");
                        result.Success = false;
                        result.Canceled = true;
                        result.Message = "已取消导入，未写入任何数据";
                        LogHelper.Instance.Info("导入在校验阶段被取消：" + fileName);
                        return result;
                    }

                    int percent = 20 + (int)((double)(i + 1) / totalRows * 30);
                    Report(reportProgress, percent, "正在校验数据... " + (i + 1).ToString() + " / " + totalRows.ToString());
                }
            }

            Report(reportProgress, 50, "校验完成，正在与库中数据比对...");

            // ---------- 4. 库内去重（双路径） ----------
            List<CodeData> finalList = new List<CodeData>();

            try
            {
                if (candidates.Count > 0)
                {
                    HashSet<string> existingSet = BuildExistingSet(candidates);

                    for (int i = 0; i < candidates.Count; i++)
                    {
                        CodeData item = candidates[i];

                        if (existingSet.Contains(item.CodeValue))
                        {
                            result.DbDuplicateCount++;
                            LogHelper.Instance.LogDuplicate(item.CodeValue, item.RowNo, "库内已存在");
                            continue;
                        }

                        finalList.Add(item);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                LogHelper.Instance.EndDuplicateSession("导入在查重阶段被取消");
                result.Success = false;
                result.Canceled = true;
                result.Message = "已取消导入，未写入任何数据";
                return result;
            }
            catch (Exception ex)
            {
                LogHelper.Instance.EndDuplicateSession("查重阶段异常中断");
                result.Success = false;
                result.Message = "查重失败，未导入任何数据：" + ex.Message;
                LogHelper.Instance.Error("导入查重失败：" + filePath, ex);
                return result;
            }

            result.DuplicateCount = result.FileDuplicateCount + result.DbDuplicateCount;
            result.ValidCount = finalList.Count;

            string summary = "文件 " + fileName + " 共重复 " + result.DuplicateCount.ToString()
                             + " 条（文件内 " + result.FileDuplicateCount.ToString()
                             + " 条，库内已存在 " + result.DbDuplicateCount.ToString() + " 条）";
            LogHelper.Instance.EndDuplicateSession(summary);

            Report(reportProgress, 65, "比对完成，准备写入数据库...");

            // ---------- 5. 写库（单事务） ----------
            string batchNo = DateTime.Now.ToString("yyyyMMddHHmmssfff");
            string importTime = Extensions.NowDbTimeString();
            long batchId = 0;

            try
            {
                SqliteHelper.ExecuteInTransaction(delegate (SqliteConnection connection, SqliteTransaction transaction)
                {
                    ImportBatch batch = new ImportBatch();
                    batch.BatchNo = batchNo;
                    batch.SourceType = sourceType;
                    batch.FileName = fileName;
                    batch.FilePath = filePath;
                    batch.TotalRows = totalRows;
                    batch.ValidCount = result.ValidCount;
                    batch.DuplicateCount = result.DuplicateCount;
                    batch.InvalidCount = result.InvalidCount;
                    batch.ImportTime = importTime;
                    batch.Operator = ConfigHelper.OperatorName;
                    batch.Remark = string.Empty;

                    batchId = ImportBatchDAL.Insert(connection, transaction, batch);

                    for (int i = 0; i < finalList.Count; i++)
                    {
                        finalList[i].BatchId = batchId;
                    }

                    int inserted = CodeDataDAL.InsertBatch(connection, transaction, finalList,
                        delegate () { return token.IsCancellationRequested; });

                    // 取消或异常都走这里：抛异常让事务回滚，台账也不会留下半截记录
                    if (token.IsCancellationRequested)
                    {
                        throw new OperationCanceledException();
                    }

                    if (inserted != finalList.Count)
                    {
                        throw new InvalidOperationException("批量插入条数不一致，预计 " + finalList.Count.ToString()
                                                           + " 条，实际 " + inserted.ToString() + " 条");
                    }

                    ImportBatchDAL.UpdateCounters(connection, transaction, batchId,
                        totalRows, result.ValidCount, result.DuplicateCount, result.InvalidCount, string.Empty);

                    Report(reportProgress, 95, "数据写入完成，正在收尾...");
                });
            }
            catch (OperationCanceledException)
            {
                result.Success = false;
                result.Canceled = true;
                result.Message = "已取消导入，未写入任何数据";
                LogHelper.Instance.Info("导入在写库阶段被取消（事务已回滚）：" + fileName);
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = "写入数据库失败，本次导入已全部回滚：" + ex.Message;
                LogHelper.Instance.Error("导入写库失败（事务已回滚）：" + filePath, ex);
                return result;
            }

            // ---------- 6. 收尾 ----------
            watch.Stop();
            result.ElapsedMs = watch.ElapsedMilliseconds;
            result.Success = true;
            result.BatchId = batchId;
            result.BatchNo = batchNo;
            result.Message = BuildSuccessMessage(result);

            Report(reportProgress, 100, "导入完成");

            LogHelper.Instance.Info("导入完成 文件=" + fileName + " 批次号=" + batchNo
                                    + " 总行=" + result.TotalRows.ToString()
                                    + " 有效=" + result.ValidCount.ToString()
                                    + " 重复=" + result.DuplicateCount.ToString()
                                    + " 无效=" + result.InvalidCount.ToString()
                                    + " 耗时=" + result.ElapsedMs.ToString() + "ms");

            return result;
        }

        // ============================================================
        // 私有辅助
        // ============================================================

        /// <summary>
        /// 构造"库内已存在码值"集合（双路径）
        /// </summary>
        private static HashSet<string> BuildExistingSet(List<CodeData> candidates)
        {
            HashSet<string> existingSet = new HashSet<string>();

            long dbTotal = CodeDataDAL.GetTotalCount();
            if (dbTotal <= 0)
            {
                return existingSet;
            }

            int threshold = ConfigHelper.DedupHashSetThreshold;

            if (dbTotal <= threshold)
            {
                // 路径一：库不大，全量读进内存比对
                List<string> allValues = CodeDataDAL.GetAllCodeValues();
                for (int i = 0; i < allValues.Count; i++)
                {
                    existingSet.Add(allValues[i]);
                }

                LogHelper.Instance.Debug("查重走内存 HashSet 路径，库内条数=" + dbTotal.ToString());
            }
            else
            {
                // 路径二：库很大，只在候选范围内分批 IN 查询
                List<string> candidateValues = new List<string>(candidates.Count);
                for (int i = 0; i < candidates.Count; i++)
                {
                    candidateValues.Add(candidates[i].CodeValue);
                }

                existingSet = CodeDataDAL.GetExistingCodeValues(candidateValues);

                LogHelper.Instance.Debug("查重走分批 IN 查询路径，库内条数=" + dbTotal.ToString()
                                         + "，候选条数=" + candidateValues.Count.ToString());
            }

            return existingSet;
        }

        /// <summary>组装给用户的成功文案</summary>
        private static string BuildSuccessMessage(ImportResult result)
        {
            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            builder.AppendLine("导入完成，耗时 " + ((double)result.ElapsedMs / 1000).ToString("0.00") + " 秒");
            builder.AppendLine();
            builder.AppendLine("文件总行数：" + result.TotalRows.ToString() + " 行");
            builder.AppendLine("成功入库：" + result.ValidCount.ToString() + " 条");
            builder.AppendLine("重复跳过：" + result.DuplicateCount.ToString() + " 条"
                               + "（文件内 " + result.FileDuplicateCount.ToString()
                               + " 条，库内已存在 " + result.DbDuplicateCount.ToString() + " 条）");
            builder.AppendLine("无效忽略：" + result.InvalidCount.ToString() + " 条"
                               + "（空行 " + result.EmptyRowCount.ToString()
                               + " 条，含中文 " + result.ChineseRowCount.ToString() + " 条）");

            if (result.DuplicateCount > 0)
            {
                builder.AppendLine();
                builder.AppendLine("重复码明细已写入 Logs 目录下的 duplicate 日志文件。");
            }

            return builder.ToString();
        }

        /// <summary>
        /// 进度回报（回调可能为 null，统一在这里判空）
        /// 【边界】回调里抛异常不能影响导入流程，因此内部 try/catch 兜住。
        /// </summary>
        private static void Report(Action<int, string>? reportProgress, int percent, string message)
        {
            if (reportProgress == null)
            {
                return;
            }

            try
            {
                int safePercent = percent;
                if (safePercent < 0)
                {
                    safePercent = 0;
                }
                if (safePercent > 100)
                {
                    safePercent = 100;
                }

                reportProgress(safePercent, message);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("进度回报失败：" + ex.Message);
            }
        }
    }
}
