import * as vscode from 'vscode';

/** What each /command asks of the model, on top of F&O Studio's tool instructions. */
const Commands: Record<string, string> = {
    design: 'Write a technical design document for the requirement below, for a D365 Finance & Operations developer and reviewer. '
        + 'Research first with the tools (find standard precedents with xpp_find_examples, read the tables and classes involved, check extensibility). '
        + 'Sections: Summary; Assumptions; Elements to create (type, name with the model prefix, purpose); Elements to extend and how (CoC, event handler, metadata extension) with the exact standard method or delegate; '
        + 'Data model (tables, fields with EDTs, indexes, relations); Security (privileges, duties, roles) and license impact; Batch/integration if needed; Test plan (SysTest classes); Risks. '
        + 'Do not create or change anything.',
    extend: 'Explain how to extend the standard behaviour described below without changing standard code. Read the standard elements first, '
        + 'pick the right mechanism (metadata extension, Chain of Command, event handler, delegate subscription, SysExtension plug-in) and say why, check that methods are wrappable/hookable, '
        + 'and show the complete extension code. Ask before creating anything.',
    explain: 'Explain the element, code or behaviour below: what it does, how it is called (use xpp_find_references), which tables it reads and writes, and its extension points. Cite element and method names.',
    review: 'Review my changes: list them with xpp_diff_changes (baseBranch main unless the user names another), read each changed element, run xpp_check_best_practice and xpp_analyze_performance on them, '
        + 'check Chain of Command wrappers call next, literal texts use labels, new menu items have privileges. Report findings by severity with element, method and fix, then a short pull request description.',
    support: 'Help investigate the error, message or call stack below. Find where the message comes from with xpp_find_message_source, read the code around it, explain the conditions that raise it, '
        + 'and list what to check in the data (offer xpp_query_data SELECTs) and setup. Do not change code unless asked.',
    performance: 'Review the performance of the element or process below: run xpp_analyze_performance on the classes, tables and forms involved, explain each finding in context, '
        + 'and, when database access is available, use xpp_query_data on sys.dm_db_missing_index_details or Query Store to confirm. Propose concrete code changes.',
};

const MaxToolRounds = 20;

/** @fno: a Copilot Chat participant that answers with F&O Studio's tools and the /command's instructions. */
export function registerChatParticipant(context: vscode.ExtensionContext): void {
    const participant = vscode.chat.createChatParticipant('xpp.fno', async (request, chatContext, stream, token) => {
        const tools = vscode.lm.tools.filter(t => /(^|_)xpp_/.test(t.name));
        const instructions = request.command ? Commands[request.command] : 'Answer the question about Dynamics 365 Finance & Operations development below, using the F&O Studio tools to look at the actual metadata and code.';
        const history = chatContext.history.flatMap(turn => turn instanceof vscode.ChatRequestTurn
            ? [vscode.LanguageModelChatMessage.User(turn.prompt)]
            : turn instanceof vscode.ChatResponseTurn
                ? [vscode.LanguageModelChatMessage.Assistant(turn.response.map(r => r instanceof vscode.ChatResponseMarkdownPart ? r.value.value : '').join(''))]
                : []);
        const messages: vscode.LanguageModelChatMessage[] = [
            ...history,
            vscode.LanguageModelChatMessage.User(`${instructions}\n\n${request.prompt}`),
        ];
        for (let round = 0; round < MaxToolRounds && !token.isCancellationRequested; round++) {
            const response = await request.model.sendRequest(messages, { tools }, token);
            const calls: vscode.LanguageModelToolCallPart[] = [];
            const parts: (vscode.LanguageModelTextPart | vscode.LanguageModelToolCallPart)[] = [];
            for await (const part of response.stream) {
                if (part instanceof vscode.LanguageModelTextPart) {
                    stream.markdown(part.value);
                    parts.push(part);
                } else if (part instanceof vscode.LanguageModelToolCallPart) {
                    calls.push(part);
                    parts.push(part);
                }
            }
            if (calls.length === 0) {
                return {};
            }
            messages.push(vscode.LanguageModelChatMessage.Assistant(parts));
            for (const call of calls) {
                stream.progress(`Running ${call.name.replace(/^.*?xpp_/, 'xpp_')}…`);
                let result: vscode.LanguageModelToolResult;
                try {
                    result = await vscode.lm.invokeTool(call.name, { input: call.input, toolInvocationToken: request.toolInvocationToken }, token);
                } catch (err) {
                    result = new vscode.LanguageModelToolResult([new vscode.LanguageModelTextPart(`Tool failed: ${(err as Error).message}`)]);
                }
                messages.push(vscode.LanguageModelChatMessage.User([new vscode.LanguageModelToolResultPart(call.callId, result.content)]));
            }
        }
        stream.markdown('\n\n_Stopped after the maximum number of tool rounds; ask me to continue._');
        return {};
    });
    participant.iconPath = vscode.Uri.joinPath(context.extensionUri, 'media', 'icon.png');
    context.subscriptions.push(participant);
}
