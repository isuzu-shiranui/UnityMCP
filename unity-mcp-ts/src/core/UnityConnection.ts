import * as net from 'net';
import { EventEmitter } from 'events';
import { JObject } from '../types/index.js';
import { McpErrorCode } from "../types/ErrorCodes.js";

/**
 * Handles TCP/IP communication between the TypeScript MCP server and Unity Editor.
 */
export class UnityConnection extends EventEmitter {
    private static instance: UnityConnection | null = null;
    private socket: net.Socket | null = null;
    private isConnected: boolean = false;
    private host: string = '127.0.0.1';
    private port: number = 27182; // Default port from McpSettings.cs
    private buffer: string = '';
    private pendingRequests: Map<string, { resolve: (value: JObject) => void, reject: (reason: Error) => void }> = new Map();
    private requestId: number = 0;
    private reconnecting: boolean = false;
    private maxReconnectAttempts: number = 3;
    private reconnectDelay: number = 1000; // milliseconds
    private connectionWarningEmitted: boolean = false;

    /**
     * Gets the singleton instance of the UnityConnection class.
     */
    public static getInstance(): UnityConnection {
        if (!UnityConnection.instance) {
            UnityConnection.instance = new UnityConnection();
        }
        return UnityConnection.instance;
    }

    private constructor() {
        super();
        // Private constructor to enforce singleton pattern

        // Add error event listener to prevent unhandled error events
        this.on('error', (err) => {
            // Error is already logged in the error event handler of the socket
            // This prevents the error from being treated as an unhandled event
        });
    }

    /**
     * Configures the connection settings.
     * @param host The host to connect to.
     * @param port The port to connect to.
     * @param maxReconnectAttempts Maximum number of reconnect attempts.
     * @param reconnectDelay Delay between reconnect attempts in milliseconds.
     */
    public configure(host: string, port: number, maxReconnectAttempts: number = 3, reconnectDelay: number = 1000): void {
        this.host = host;
        this.port = port;
        this.maxReconnectAttempts = maxReconnectAttempts;
        this.reconnectDelay = reconnectDelay;
    }

    /**
     * Connects to the Unity TCP server.
     * @returns A promise that resolves when connected or rejects if connection fails.
     */
    public connect(): Promise<void> {
        return new Promise((resolve, reject) => {
            if (this.isConnected && this.socket) {
                resolve();
                return;
            }

            // Create new socket
            this.socket = new net.Socket();

            // Set up event handlers
            this.socket.on('data', (data) => this.handleData(data));

            this.socket.on('close', () => {
                console.error('[INFO] Disconnected from Unity server');
                this.isConnected = false;
                this.socket = null;
                this.emit('disconnected');

                // Reject all pending requests
                for (const [id, { reject }] of this.pendingRequests) {
                    reject(new Error('Connection closed'));
                    this.pendingRequests.delete(id);
                }
            });

            this.socket.on('error', (err) => {
                console.error(`[ERROR] Socket error: ${err.message}`);
                // Don't reject if we're not connected yet to allow retrying
                // This is the key change - we don't propagate the error to cause process termination
                if (!this.isConnected) {
                    reject(err);
                }

                // Emit error event, but it will be caught by our listener
                this.emit('error', err);

                // Close the socket to prevent multiple error events
                if (this.socket) {
                    this.socket.destroy();
                    this.socket = null;
                }

                this.isConnected = false;
            });

            // Connect to Unity server
            this.socket.connect(this.port, this.host, () => {
                console.error(`[INFO] Connected to Unity server at ${this.host}:${this.port}`);
                this.isConnected = true;
                this.connectionWarningEmitted = false;
                this.emit('connected');
                resolve();
            });
        });
    }

    /**
     * Attempts to reconnect to the Unity server.
     * @param attempts Number of attempts to make (defaults to maxReconnectAttempts).
     * @returns A promise that resolves when reconnected or rejects if all attempts fail.
     */
    public async reconnect(attempts: number = this.maxReconnectAttempts): Promise<void> {
        if (this.isConnected) {
            return Promise.resolve();
        }

        if (this.reconnecting) {
            // Return a promise that waits for the current reconnection attempt
            return new Promise((resolve, reject) => {
                const checkConnection = () => {
                    if (this.isConnected) {
                        this.removeListener('connected', onConnected);
                        this.removeListener('reconnectFailed', onFailed);
                        resolve();
                    }
                };

                const onConnected = () => {
                    this.removeListener('reconnectFailed', onFailed);
                    resolve();
                };

                const onFailed = () => {
                    this.removeListener('connected', onConnected);
                    reject(new Error('Reconnection failed'));
                };

                // Check if connection has already been established
                if (this.isConnected) {
                    resolve();
                    return;
                }

                // Listen for connection events
                this.once('connected', onConnected);
                this.once('reconnectFailed', onFailed);
            });
        }

        this.reconnecting = true;
        console.error(`[INFO] Attempting to reconnect to Unity server at ${this.host}:${this.port}`);

        // Disconnect if there's an existing connection
        if (this.socket) {
            this.disconnect();
        }

        let remainingAttempts = attempts;
        while (remainingAttempts > 0 && !this.isConnected) {
            try {
                await this.connect();
                this.reconnecting = false;
                console.error('[INFO] Successfully reconnected to Unity server');
                return;
            } catch (err) {
                console.error(`[ERROR] Reconnection attempt failed: ${err instanceof Error ? err.message : String(err)}`);
                remainingAttempts--;

                if (remainingAttempts > 0) {
                    console.error(`[INFO] Waiting ${this.reconnectDelay}ms before next reconnection attempt. ${remainingAttempts} attempts remaining.`);
                    await new Promise(resolve => setTimeout(resolve, this.reconnectDelay));
                }
            }
        }

        this.reconnecting = false;
        this.emit('reconnectFailed');
        const error = new Error(`Failed to reconnect after ${attempts} attempts`);
        (error as any).code = McpErrorCode.ConnectionError;
        throw error;
    }

    /**
     * Ensures that there is an active connection to Unity, attempting to reconnect if necessary.
     * @returns A promise that resolves when connected or rejects if connection cannot be established.
     */
    public async ensureConnected(): Promise<void> {
        if (this.isConnected) {
            return Promise.resolve();
        }

        return this.reconnect();
    }

    /**
     * Sends a request to Unity and waits for a response.
     * @param request The request object to send.
     * @param autoReconnect Whether to attempt reconnection if not connected.
     * @returns A Promise that resolves with the response, or rejects if the request fails.
     */
    public async sendRequest(request: JObject, autoReconnect: boolean = true): Promise<JObject> {
        if (!this.isConnected || !this.socket) {
            if (autoReconnect) {
                try {
                    await this.reconnect();
                } catch (err) {
                    // Only emit the warning once to avoid log spam
                    if (!this.connectionWarningEmitted) {
                        console.error(`[WARN] Unable to connect to Unity server. Some features may not be available.`);
                        this.connectionWarningEmitted = true;
                    }
                    const error = new Error(`Failed to connect to Unity server: ${err instanceof Error ? err.message : String(err)}`);
                    (error as any).code = McpErrorCode.ConnectionError;
                    throw error;
                }
            } else {
                const error = new Error('Not connected to Unity server');
                (error as any).code = McpErrorCode.ConnectionError;
                throw error;
            }
        }

        return new Promise((resolve, reject) => {
            try {
                // Add request ID for tracking
                const id = (++this.requestId).toString();
                const requestWithId: JObject = {
                    command: request.command,
                    type: request.type || '',
                    params: request.params,
                    id
                };

                console.error(`[DEBUG] Sending request: ${JSON.stringify(requestWithId)}`);

                // Store the promise callbacks
                this.pendingRequests.set(id, { resolve, reject });

                // Send the request
                const data = JSON.stringify(requestWithId) + '\n';
                this.socket!.write(data, (err) => {
                    if (err) {
                        console.error(`[ERROR] Failed to send data to Unity: ${err.message}`);
                        this.pendingRequests.delete(id);
                        reject(err);
                    }
                });

                // Set timeout to prevent hanging requests
                setTimeout(() => {
                    if (this.pendingRequests.has(id)) {
                        console.error(`[ERROR] Request with ID ${id} timed out`);
                        this.pendingRequests.delete(id);
                        reject(new Error('Request timed out'));
                    }
                }, 30000); // 30 seconds timeout
            } catch (err) {
                console.error(`[ERROR] Error sending request: ${err instanceof Error ? err.message : String(err)}`);
                reject(err);
            }
        });
    }

    /**
     * Disconnects from the Unity server.
     */
    public disconnect(): void {
        if (this.socket) {
            this.socket.destroy();
            this.socket = null;
        }

        this.isConnected = false;
        this.emit('disconnected');

        // Reject all pending requests
        for (const [id, { reject }] of this.pendingRequests) {
            reject(new Error('Connection closed'));
            this.pendingRequests.delete(id);
        }
    }

    /**
     * Checks if connected to the Unity server.
     * @returns True if connected, false otherwise.
     */
    public isUnityConnected(): boolean {
        return this.isConnected && this.socket !== null;
    }

    /**
     * Handles data received from Unity.
     * @param data The data received.
     */
    private handleData(data: Buffer): void {
        // Add received data to buffer
        this.buffer += data.toString('utf8');

        // Try to process complete messages by newline delimiter
        let endIndex: number;
        while ((endIndex = this.buffer.indexOf('\n')) !== -1) {
            // Extract a complete message
            const message = this.buffer.substring(0, endIndex).trim();
            // Remove the processed message from the buffer
            this.buffer = this.buffer.substring(endIndex + 1);

            // Process the message
            this.processMessage(message);
        }

        // Check if there's data in the buffer that might be a complete message without newline
        if (this.buffer.length > 0) {
            try {
                // Try to parse as JSON to see if it's complete
                JSON.parse(this.buffer);

                // If we reach here, it's valid JSON, so process it
                const message = this.buffer.trim();
                this.buffer = '';
                this.processMessage(message);
            } catch (err) {
                // Not complete JSON, keep waiting for more data
                // This is expected if we're in the middle of receiving a message
            }
        }
    }

    /**
     * Processes a complete message.
     * @param message The complete message to process.
     */
    private processMessage(message: string): void {
        if (!message) {
            return;
        }

        try {
            console.error(`[DEBUG] Processing message: ${message}`);
            const response = JSON.parse(message) as JObject;

            // Check if this is a response with status and result fields (Unity format)
            if (response.status === "success" && response.result && response.id) {
                const id = response.id as string;
                const result = response.result as JObject;

                // Check if this is a response to a pending request
                if (this.pendingRequests.has(id)) {
                    const { resolve } = this.pendingRequests.get(id)!;
                    this.pendingRequests.delete(id);
                    resolve(result);
                }
            }
            // Regular response with just an ID
            else if (response.id) {
                const id = response.id as string;

                // Check if this is a response to a pending request
                if (this.pendingRequests.has(id)) {
                    const { resolve } = this.pendingRequests.get(id)!;
                    this.pendingRequests.delete(id);
                    resolve(response);
                }
            }
            // This is a push notification or event from Unity
            else {
                this.emit('message', response);
            }
        } catch (err) {
            console.error(`[ERROR] Failed to parse message: ${err instanceof Error ? err.message : String(err)}`);
        }
    }
}
