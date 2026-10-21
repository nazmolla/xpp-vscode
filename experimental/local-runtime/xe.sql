IF EXISTS (SELECT 1 FROM sys.server_event_sessions WHERE name = 'XppLocal') DROP EVENT SESSION XppLocal ON SERVER;
CREATE EVENT SESSION XppLocal ON SERVER
ADD EVENT sqlserver.error_reported (ACTION (sqlserver.sql_text, sqlserver.database_name) WHERE severity >= 11),
ADD EVENT sqlserver.rpc_completed (ACTION (sqlserver.database_name) WHERE sqlserver.database_name = N'AxDbLocalTest'),
ADD EVENT sqlserver.sql_batch_completed (ACTION (sqlserver.database_name) WHERE sqlserver.database_name = N'AxDbLocalTest')
ADD TARGET package0.event_file (SET filename = N'C:\XppLocalRuntime\xe\xpplocal.xel')
WITH (MAX_DISPATCH_LATENCY = 1 SECONDS);
ALTER EVENT SESSION XppLocal ON SERVER STATE = START;
