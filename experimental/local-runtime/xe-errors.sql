SET NOCOUNT ON;
SELECT DISTINCT LEFT(x.value('(event/data[@name="message"]/value)[1]','nvarchar(max)'),80) AS msg,
       LEFT(x.value('(event/action[@name="sql_text"]/value)[1]','nvarchar(max)'), 300) AS sqltext
FROM (SELECT CAST(event_data AS xml) x FROM sys.fn_xe_file_target_read_file('C:\XppLocalRuntime\xe\xpplocal*.xel', NULL, NULL, NULL)) t
WHERE x.value('(event/@name)[1]','nvarchar(60)') = 'error_reported';
