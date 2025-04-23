import { McpErrorCode } from "./ErrorCodes.js";

/**
 * Represents a JSON object with string keys and various value types.
 */
export type JObject = {
    [key: string]: string | number | boolean | null | JObject | Array<any>;
};

/**
 * Represents a command response
 */
export interface ErrorResponse {
    success: false;
    error: string;
    errorCode?: McpErrorCode;
    errorDetails?: {
        command?: string;
        timestamp: string;
        type: string;
        [key: string]: any;
    };
}

/**
 * Represents a successful command response
 */
export interface SuccessResponse {
    success: true;
    [key: string]: any;
}

/**
 * Represents a command response that can be either a success or an error
 */
export type CommandResponse = SuccessResponse | ErrorResponse;
