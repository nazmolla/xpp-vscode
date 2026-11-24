import { Dataverse } from './core';
import { dataverseTokens } from './ude';
import { selectedEnvironmentUrl } from './environment';

export { Dataverse };

/** The Dataverse org this window works with, signed in through VS Code's Microsoft account; fails with what to do when none is selected. */
export async function selectedDataverse(): Promise<Dataverse> {
    const environmentUrl = selectedEnvironmentUrl();
    if (!environmentUrl) {
        throw new Error('No environment selected. Run "F&O Studio: Connect to Environment" or "F&O Studio: Select Environment" first.');
    }
    return Dataverse.connect(environmentUrl, dataverseTokens);
}
