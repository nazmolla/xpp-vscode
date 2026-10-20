using System;
using System.IO;
using System.Text;

namespace XppVsCode.Server.Tools
{
    /// <summary>
    /// Centralized logging for the MCP server process.
    /// </summary>
    internal static class McpLogger
    {
        private static readonly string LogFilePath =
            Path.Combine(Path.GetTempPath(), "XppMcpServer.log");

        public static void Log(string message)
        {
            try
            {
                File.AppendAllText(
                    LogFilePath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch
            {
                // Never let diagnostics logging break MCP behavior.
            }
        }
    }
}
