import * as vscode from 'vscode';
import { elementOf, TreeArg } from './d365Tools';

// KQL for the telemetry Finance and Operations sends to Application Insights (Monitoring and telemetry feature).
// Form loads are pageViews named after the form; X++ exceptions go to the exceptions table
// (learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/monitoring-telemetry/monitoring-analyze-telemetry-kql).
export function telemetryQueries(formName?: string): string {
    const form = formName ? `\n| where name == "${formName.replace(/[^A-Za-z0-9_]/g, '')}"` : '\n| where name !in ("SysOperationSandboxForm", "SysBoxForm", "Dialog")';
    return `// Finance and Operations telemetry in Application Insights (Logs).
// In a Log Analytics workspace use AppPageViews, AppExceptions, AppEvents and AppTraces instead.

// Most opened forms
pageViews
| where timestamp > ago(7d)${form}
| summarize ['Form opens'] = count() by name
| top 20 by ['Form opens'] desc

// Slowest form loads
pageViews
| where timestamp > ago(7d) and duration > 0${form}
| summarize ['Opens'] = count(), ['Avg (s)'] = round(avg(duration) / 1000, 2), ['P95 (s)'] = round(percentile(duration, 95) / 1000, 2), ['Max (s)'] = round(max(duration) / 1000, 2) by name
| order by ['P95 (s)'] desc

// Most frequent X++ exceptions
exceptions
| where timestamp > ago(7d)
| summarize ['Count'] = count(), ['Users'] = dcount(user_Id), ['Last'] = max(timestamp) by problemId, outerMessage
| top 20 by ['Count'] desc

// Exceptions per hour (spikes)
exceptions
| where timestamp > ago(7d)
| summarize count() by bin(timestamp, 1h)
| render timechart

// Custom events (SysApplicationInsightsTelemetryLogger.trackEvent, DMF jobs)
customEvents
| where timestamp > ago(7d)
| summarize count() by name
| order by count_ desc
`;
}

export function registerTelemetryQueries(command: (id: string, handler: (...args: any[]) => unknown) => void): void {
    command('xpp.telemetryQueries', async (arg?: TreeArg) => {
        const element = elementOf(arg);
        const formName = element?.objectType === 'AxForm' ? element.objectName : undefined;
        const language = (await vscode.languages.getLanguages()).includes('kusto') ? 'kusto' : 'plaintext';
        const doc = await vscode.workspace.openTextDocument({ language, content: telemetryQueries(formName) });
        await vscode.window.showTextDocument(doc);
    });
}
