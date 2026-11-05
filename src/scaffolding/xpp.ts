/** One element a wizard creates through the metadata API (the body is create_object's request). */
export interface ElementSpec {
    objectType: string;
    objectName: string;
    body: Record<string, unknown>;
}

export const NamePattern = /^[A-Za-z][A-Za-z0-9_]{2,60}$/;

/** Doc comment block for generated X++. */
export const doc = (summary: string, extra = '') => `/// <summary>\n/// ${summary}\n/// </summary>\n${extra}`;

/** X++ string literal of a label reference or text. */
export const xppString = (text: string) => `"${text.replace(/["\\]/g, '\\$&')}"`;

/** camelCase variable name for a type name. */
export const lower = (name: string) => name.charAt(0).toLowerCase() + name.slice(1);
