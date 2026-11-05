/** Live performance checks on the environment's database, as read-only queries the query panel runs (verified on a UDE with Reader credentials). */
export const Insights: { label: string; detail: string; sql: string }[] = [
    {
        label: 'Missing indexes',
        detail: 'Indexes SQL Server would have used, by estimated benefit (add them as table indexes in your model, not in SQL)',
        sql: `SELECT TOP 25 CONVERT(decimal(18, 1), s.avg_user_impact * (s.user_seeks + s.user_scans)) AS Benefit,
  OBJECT_NAME(d.object_id) AS TableName, d.equality_columns AS EqualityColumns, d.inequality_columns AS InequalityColumns,
  d.included_columns AS IncludedColumns, s.user_seeks AS Seeks, s.avg_user_impact AS ImpactPercent, s.last_user_seek AS LastSeek
FROM sys.dm_db_missing_index_details d
JOIN sys.dm_db_missing_index_groups g ON g.index_handle = d.index_handle
JOIN sys.dm_db_missing_index_group_stats s ON s.group_handle = g.index_group_handle
WHERE d.database_id = DB_ID()
ORDER BY Benefit DESC`,
    },
    {
        label: 'Most expensive queries (last 24 hours)',
        detail: 'Query Store: total duration, executions and the slowest run; select a query and use Explain Plan',
        sql: `SELECT TOP 20 q.query_id AS QueryId, SUM(rs.count_executions) AS Executions,
  CONVERT(decimal(18, 1), SUM(rs.avg_duration * rs.count_executions) / 1000.0) AS TotalMs,
  CONVERT(decimal(18, 1), MAX(rs.max_duration) / 1000.0) AS MaxMs,
  CONVERT(decimal(18, 1), SUM(rs.avg_logical_io_reads * rs.count_executions)) AS LogicalReads,
  MAX(CAST(qt.query_sql_text AS nvarchar(2000))) AS QueryText
FROM sys.query_store_runtime_stats rs
JOIN sys.query_store_runtime_stats_interval i ON i.runtime_stats_interval_id = rs.runtime_stats_interval_id
JOIN sys.query_store_plan p ON p.plan_id = rs.plan_id
JOIN sys.query_store_query q ON q.query_id = p.query_id
JOIN sys.query_store_query_text qt ON qt.query_text_id = q.query_text_id
WHERE i.start_time > DATEADD(day, -1, SYSUTCDATETIME())
GROUP BY q.query_id
ORDER BY TotalMs DESC`,
    },
    {
        label: 'Slowest batch jobs (last 7 days)',
        detail: 'Batch job history: runs, average and longest duration per job and company',
        sql: `SELECT TOP 25 CAPTION AS Job, COMPANY AS Company, COUNT(*) AS Runs,
  AVG(DATEDIFF(second, STARTDATETIME, ENDDATETIME)) AS AvgSeconds,
  MAX(DATEDIFF(second, STARTDATETIME, ENDDATETIME)) AS MaxSeconds
FROM BATCHJOBHISTORY
WHERE STARTDATETIME > DATEADD(day, -7, GETUTCDATE()) AND ENDDATETIME > STARTDATETIME
GROUP BY CAPTION, COMPANY
ORDER BY MaxSeconds DESC`,
    },
    {
        label: 'Number sequences that serialize work',
        detail: 'Continuous sequences (a lock per number) and non-continuous ones without preallocation',
        sql: `SELECT NUMBERSEQUENCE AS NumberSequence, CONTINUOUS AS Continuous, FETCHAHEAD AS Preallocation, FETCHAHEADQTY AS PreallocationQty,
  CASE WHEN CONTINUOUS = 1 THEN 'Continuous: numbers are taken one at a time under a lock; keep it only where gaps are not allowed'
       ELSE 'No preallocation: each number is a database round trip; consider preallocating' END AS Review
FROM NUMBERSEQUENCETABLE
WHERE CONTINUOUS = 1 OR FETCHAHEAD = 0
ORDER BY CONTINUOUS DESC, NUMBERSEQUENCE`,
    },
];
