using System.Diagnostics;
using Microsoft.Data.Sqlite;
using InkPrinterCode.Common;
using InkPrinterCode.DAL;
using InkPrinterCode.Model;

namespace InkPrinterCode.BLL
{
    /// <summary>
    /// [2026-09-10] Data import business layer — read file → validate → deduplicate → insert in a single transaction → write the batch record
    ///
    /// [Full pipeline]
    ///   1. File existence check + extension routing (.txt / .xls / .xlsx; anything else is rejected outright, never guess the format)
    ///   2. Read the source file (line by line for txt / first column for Excel); blank lines are kept as placeholders so that row numbers line up
    ///   3. Clean each line (Trim only) and validate: blank row, contains Chinese → counted as invalid and skipped
    ///   4. In-file deduplication (HashSet, keep the first occurrence)
    ///   5. In-database deduplication (dual path, see the note below)
    ///   6. Single-transaction write: first insert the batch record to obtain BatchId, then bulk-insert the codes, finally write the counters back to the batch record
    ///
    /// [Dual-path deduplication]
    ///   Total row count in the database &lt;= DedupHashSetThreshold (default 1,000,000) → read all codes in the database into an in-memory
    ///     HashSet at once for comparison (fastest);
    ///   above the threshold → only run batched IN queries within the candidate code range (500 parameters per batch), trading a little speed
    ///     for memory safety.
    ///   The two paths produce exactly the same result; the only difference is memory usage.
    ///
    /// [Cancellation and rollback]
    ///   Read phase: the token is passed straight to TxtHelper / ExcelHelper; cancellation immediately throws OperationCanceledException;
    ///   Write phase: the token is checked before each batch starts; once cancelled, the insert is aborted and an exception is thrown so that
    ///             the whole transaction rolls back — "half-imported" data must never occur (partial data is worse than no import at all,
    ///             since on site there is no way to tell which rows did not make it in).
    ///
    /// [Edge case]
    ///   Even with 0 valid records (for example everything was a duplicate code), the batch record is still written, so it is traceable that
    ///   "this file was imported and everything was deduplicated".
    /// </summary>
    public static class ImportBLL
    {
        /// <summary>
        /// Execute the import
        /// </summary>
        /// <param name="filePath">Full path of the source file</param>
        /// <param name="token">Cancellation signal, held and triggered by the caller (the progress form)</param>
        /// <param name="reportProgress">Progress callback: parameter 1 = percentage (0~100), parameter 2 = current phase description text</param>
        /// <returns>Import result (the three states success / failure / cancelled are described in the ImportResult comments)</returns>
        public static ImportResult ImportFromFile(string filePath, CancellationToken token, Action<int, string>? reportProgress)
        {
            ImportResult result = new ImportResult();
            Stopwatch watch = new Stopwatch();
            watch.Start();

            // ---------- 1. Pre-checks ----------
            if (!ValidateHelper.IsFileExists(filePath))
            {
                result.Success = false;
                result.Message = "File does not exist or is not accessible: " + filePath;
                LogHelper.Instance.Warn("Import rejected, file does not exist: " + filePath);
                return result;
            }

            bool supported = false;
            ImportSourceType sourceType = EnumHelper.GetSourceTypeByExtension(filePath, out supported);
            if (!supported)
            {
                result.Success = false;
                result.Message = "Unsupported file format, only .txt / .xls / .xlsx are supported";
                LogHelper.Instance.Warn("Import rejected, unsupported format: " + filePath);
                return result;
            }

            string fileName = Path.GetFileName(filePath);
            LogHelper.Instance.Info("Import started file=" + fileName + " type=" + EnumHelper.GetSourceTypeText(sourceType) + " path=" + filePath);

            // ---------- 2. Read the source file ----------
            List<string> rawLines = new List<string>();
            int totalRows = 0;

            try
            {
                Report(reportProgress, 5, "Reading file...");

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
                result.Message = "Import cancelled; no data was written";
                LogHelper.Instance.Info("Import cancelled during the read phase: " + fileName);
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = "Failed to read file: " + ex.Message;
                LogHelper.Instance.Error("Failed to read import file: " + filePath, ex);
                return result;
            }

            result.TotalRows = totalRows;
            Report(reportProgress, 20, "File read complete, " + totalRows.ToString() + " rows in total, validating...");

            if (totalRows <= 0)
            {
                result.Success = false;
                result.Message = "The file has no content; nothing was imported";
                LogHelper.Instance.Warn("Import file is empty: " + filePath);
                return result;
            }

            // ---------- 3. Clean + validate + in-file deduplication ----------
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
                    if (invalidReason == "Empty row")
                    {
                        result.EmptyRowCount++;
                    }
                    else
                    {
                        result.ChineseRowCount++;
                    }
                    continue;
                }

                // Duplicate within the file: keep the first occurrence, subsequent occurrences of the same code count as duplicates
                if (!fileSet.Add(codeValue))
                {
                    result.FileDuplicateCount++;
                    LogHelper.Instance.LogDuplicate(codeValue, rowNo, "Duplicate in file");
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
                        LogHelper.Instance.EndDuplicateSession("Import cancelled during the validation phase");
                        result.Success = false;
                        result.Canceled = true;
                        result.Message = "Import cancelled; no data was written";
                        LogHelper.Instance.Info("Import cancelled during the validation phase: " + fileName);
                        return result;
                    }

                    int percent = 20 + (int)((double)(i + 1) / totalRows * 30);
                    Report(reportProgress, percent, "Validating data... " + (i + 1).ToString() + " / " + totalRows.ToString());
                }
            }

            Report(reportProgress, 50, "Validation complete, comparing against data in the database...");

            // ---------- 4. In-database deduplication (dual path) ----------
            List<CodeData> finalList = new List<CodeData>();

            try
            {
                if (candidates.Count > 0)
                {
                    HashSet<string> existingSet = BuildExistingSet(candidates, token);

                    for (int i = 0; i < candidates.Count; i++)
                    {
                        CodeData item = candidates[i];

                        if (existingSet.Contains(item.CodeValue))
                        {
                            result.DbDuplicateCount++;
                            LogHelper.Instance.LogDuplicate(item.CodeValue, item.RowNo, "Already exists in database");
                            continue;
                        }

                        finalList.Add(item);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                LogHelper.Instance.EndDuplicateSession("Import cancelled during the deduplication phase");
                result.Success = false;
                result.Canceled = true;
                result.Message = "Import cancelled; no data was written";
                return result;
            }
            catch (Exception ex)
            {
                LogHelper.Instance.EndDuplicateSession("Deduplication phase aborted by an exception");
                result.Success = false;
                result.Message = "Deduplication failed; nothing was imported: " + ex.Message;
                LogHelper.Instance.Error("Import deduplication failed: " + filePath, ex);
                return result;
            }

            result.DuplicateCount = result.FileDuplicateCount + result.DbDuplicateCount;
            result.ValidCount = finalList.Count;

            string summary = "File " + fileName + " has " + result.DuplicateCount.ToString()
                             + " duplicates in total (" + result.FileDuplicateCount.ToString()
                             + " in file, " + result.DbDuplicateCount.ToString() + " already in database)";
            LogHelper.Instance.EndDuplicateSession(summary);

            Report(reportProgress, 65, "Comparison complete, preparing to write to the database...");

            // ---------- 5. Write to the database (single transaction) ----------
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

                    // Both cancellation and exceptions take this route: throw so the transaction rolls back, and the batch record
                    // will not be left half-written either
                    if (token.IsCancellationRequested)
                    {
                        throw new OperationCanceledException();
                    }

                    if (inserted != finalList.Count)
                    {
                        throw new InvalidOperationException("Bulk insert count mismatch: expected " + finalList.Count.ToString()
                                                           + " records, actual " + inserted.ToString() + " records");
                    }

                    ImportBatchDAL.UpdateCounters(connection, transaction, batchId,
                        totalRows, result.ValidCount, result.DuplicateCount, result.InvalidCount, string.Empty);

                    Report(reportProgress, 95, "Data written, finishing up...");
                });
            }
            catch (OperationCanceledException)
            {
                result.Success = false;
                result.Canceled = true;
                result.Message = "Import cancelled; no data was written";
                LogHelper.Instance.Info("Import cancelled during the write phase (transaction rolled back): " + fileName);
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = "Failed to write to the database; this import was fully rolled back: " + ex.Message;
                LogHelper.Instance.Error("Import write failed (transaction rolled back): " + filePath, ex);
                return result;
            }

            // ---------- 6. Wrap-up ----------
            watch.Stop();
            result.ElapsedMs = watch.ElapsedMilliseconds;
            result.Success = true;
            result.BatchId = batchId;
            result.BatchNo = batchNo;
            result.Message = BuildSuccessMessage(result);

            Report(reportProgress, 100, "Import complete");

            LogHelper.Instance.Info("Import complete file=" + fileName + " batch no.=" + batchNo
                                    + " total rows=" + result.TotalRows.ToString()
                                    + " valid=" + result.ValidCount.ToString()
                                    + " duplicate=" + result.DuplicateCount.ToString()
                                    + " invalid=" + result.InvalidCount.ToString()
                                    + " elapsed=" + result.ElapsedMs.ToString() + "ms");

            return result;
        }

        // ============================================================
        // Private helpers
        // ============================================================

        /// <summary>
        /// Build the set of "code values already in the database" (dual path)
        /// </summary>
        private static HashSet<string> BuildExistingSet(List<CodeData> candidates, CancellationToken token)
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
                // Path 1: the database is small, read everything into memory for comparison
                List<string> allValues = CodeDataDAL.GetAllCodeValues(delegate () { return token.IsCancellationRequested; });
                for (int i = 0; i < allValues.Count; i++)
                {
                    existingSet.Add(allValues[i]);
                }

                LogHelper.Instance.Debug("Deduplication uses the in-memory HashSet path, database row count=" + dbTotal.ToString());
            }
            else
            {
                // Path 2: the database is large, only run batched IN queries within the candidate range
                List<string> candidateValues = new List<string>(candidates.Count);
                for (int i = 0; i < candidates.Count; i++)
                {
                    candidateValues.Add(candidates[i].CodeValue);
                }

                existingSet = CodeDataDAL.GetExistingCodeValues(candidateValues, delegate () { return token.IsCancellationRequested; });

                LogHelper.Instance.Debug("Deduplication uses the batched IN query path, database row count=" + dbTotal.ToString()
                                         + ", candidate row count=" + candidateValues.Count.ToString());
            }

            // [2026-09-16] P2: propagate cancellation out of the dedup phase (mirrors the write-phase pattern in ImportFromFile)
            if (token.IsCancellationRequested)
            {
                throw new OperationCanceledException();
            }

            return existingSet;
        }

        /// <summary>Assemble the success message shown to the user</summary>
        private static string BuildSuccessMessage(ImportResult result)
        {
            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            builder.AppendLine("Import complete, elapsed " + ((double)result.ElapsedMs / 1000).ToString("0.00") + " seconds");
            builder.AppendLine();
            builder.AppendLine("Total rows in file: " + result.TotalRows.ToString() + " rows");
            builder.AppendLine("Successfully imported: " + result.ValidCount.ToString() + " records");
            builder.AppendLine("Duplicates skipped: " + result.DuplicateCount.ToString() + " records"
                               + " (" + result.FileDuplicateCount.ToString()
                               + " in file, " + result.DbDuplicateCount.ToString() + " already in database)");
            builder.AppendLine("Invalid ignored: " + result.InvalidCount.ToString() + " records"
                               + " (empty rows " + result.EmptyRowCount.ToString()
                               + ", containing Chinese " + result.ChineseRowCount.ToString() + ")");

            if (result.DuplicateCount > 0)
            {
                builder.AppendLine();
                builder.AppendLine("The duplicate code details have been written to the duplicate log file under the Logs directory.");
            }

            return builder.ToString();
        }

        /// <summary>
        /// Progress reporting (the callback may be null, so null checks are centralized here)
        /// [Edge case] An exception thrown inside the callback must not affect the import flow, so an internal try/catch absorbs it.
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
                System.Diagnostics.Debug.WriteLine("Progress reporting failed: " + ex.Message);
            }
        }
    }
}
