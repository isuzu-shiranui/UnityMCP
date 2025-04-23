export enum McpErrorCode {
    // Standard JSON-RPC error codes
    ParseError = -32700,
    InvalidRequest = -32600,
    MethodNotFound = -32601,
    InvalidParams = -32602,
    InternalError = -32603,

    // Application specific error codes (must be > -32000)
    CommandExecutionError = 100,
    ConnectionError = 101,
    ResourceNotFound = 102,
    ToolExecutionError = 103,
    UnityConnectionError = 104
}

export interface McpError {
    code: McpErrorCode;
    message: string;
    data?: any;
}
