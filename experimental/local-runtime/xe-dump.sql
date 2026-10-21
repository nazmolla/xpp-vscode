SET NOCOUNT ON;
SELECT t.file_offset, x.value('(event/@timestamp)[1]','nvarchar(40)') ts, REPLACE(REPLACE(COALESCE(x.value('(event/data[@name="statement"]/value)[1]','nvarchar(max)'), x.value('(event/data[@name="batch_text"]/value)[1]','nvarchar(max)'), x.value('(event/data[@name="message"]/value)[1]','nvarchar(max)')),CHAR(13),' '),CHAR(10),' ') AS txt
FROM (SELECT CAST(event_data AS xml) x, file_offset FROM sys.fn_xe_file_target_read_file('C:\XppLocalRuntime\xe\xpplocal*.xel', NULL, NULL, NULL)) t
ORDER BY ts;
