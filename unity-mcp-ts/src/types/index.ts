/**
 * Represents a JSON object with string keys and various value types.
 */
export type JObject = {
    [key: string]: string | number | boolean | null | JObject | Array<any>;
};
