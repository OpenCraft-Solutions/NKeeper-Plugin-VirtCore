import type { StreamConnection } from '@nkeeper/sdk'

/** A message from a tool: a `reply` or `error` to a request, or one of the tool's own (`size`, `entry`, `ack`…). */
export interface ToolMessage {
  type: string
  id?: string
  data?: unknown
  message?: string
  [key: string]: unknown
}

export class ToolError extends Error {}

/**
 * The tool protocol db-core shares with the linux, storage and windows plugins, on the session's stream (docs/architecture.md §7.25, §7.32): each request carries an `id`,
 * answered by `reply` `{ id, data }` or `error` `{ id, message }`. A `fatal` message means the tool can't go on.
 */
export class ToolClient {
  private next = 0
  private readonly pending = new Map<string, { resolve: (data: unknown) => void; reject: (error: Error) => void }>()
  private readonly listeners = new Set<(message: ToolMessage) => void>()
  private readonly stop: () => void
  private readonly connection: StreamConnection

  constructor(connection: StreamConnection) {
    this.connection = connection
    this.stop = connection.onText((text) => this.receive(text))
    void connection.closed.then(() => this.failAll('The session ended.'))
  }

  /** A new request ID, for requests whose other messages (sizes, entries) a view listens for. */
  newId(): string {
    this.next += 1
    return `r${this.next}`
  }

  /** Sends a request and waits for its reply. */
  request<T = unknown>(type: string, body: Record<string, unknown> = {}, id = this.newId()): Promise<T> {
    return new Promise<T>((resolve, reject) => {
      this.pending.set(id, { resolve: (data) => resolve(data as T), reject })
      this.connection.sendJson({ ...body, type, id })
    })
  }

  /** Every message, replies included. Returns a function that stops listening. */
  on(listener: (message: ToolMessage) => void): () => void {
    this.listeners.add(listener)
    return () => this.listeners.delete(listener)
  }

  send(data: Uint8Array<ArrayBuffer>): void {
    this.connection.send(data)
  }

  /** Binary frames: a console's bytes. Returns a function that stops listening. */
  onBinary(listener: (data: Uint8Array) => void): () => void {
    return this.connection.onBinary(listener)
  }

  close(): void {
    this.stop()
    this.failAll('The session ended.')
    this.connection.close()
  }

  private receive(text: string) {
    let message: ToolMessage
    try {
      message = JSON.parse(text) as ToolMessage
    } catch {
      return
    }
    if (typeof message !== 'object' || message === null || typeof message.type !== 'string') {
      return
    }
    const waiting = typeof message.id === 'string' ? this.pending.get(message.id) : undefined
    if (waiting && (message.type === 'reply' || message.type === 'error')) {
      this.pending.delete(message.id!)
      if (message.type === 'reply') {
        waiting.resolve(message.data)
      } else {
        waiting.reject(new ToolError(typeof message.message === 'string' ? message.message : 'The host refused.'))
      }
    }
    if (message.type === 'fatal') {
      this.failAll(typeof message.message === 'string' ? message.message : 'The tool stopped.')
    }
    this.listeners.forEach((listener) => listener(message))
  }

  private failAll(reason: string) {
    const waiting = [...this.pending.values()]
    this.pending.clear()
    waiting.forEach((w) => w.reject(new ToolError(reason)))
  }
}
