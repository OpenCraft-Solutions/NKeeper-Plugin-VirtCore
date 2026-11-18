import type { ToolClient } from './client'

type Handler<T> = ((event: T) => void) | null

/**
 * A guest's console as a WebSocket to noVNC: its bytes ride the guests session's binary frames
 * (docs/architecture.md §7.36), so the console needs no socket of its own. noVNC checks that a channel has these
 * members, as a WebSocket or an RTCDataChannel has them.
 */
export class ConsoleChannel {
  binaryType = 'arraybuffer'
  protocol = ''
  readyState: 'open' | 'closed' = 'open'
  onopen: Handler<Event> = null
  onmessage: Handler<{ data: ArrayBuffer }> = null
  onclose: Handler<{ code: number; reason: string; wasClean: boolean }> = null
  onerror: Handler<Event> = null
  private readonly client: ToolClient
  private readonly stop: () => void

  constructor(client: ToolClient) {
    this.client = client
    this.stop = client.onBinary((data) => {
      if (this.readyState === 'open') {
        // A copy of just these bytes: the frame may be a view of a bigger buffer.
        this.onmessage?.({ data: data.slice().buffer as ArrayBuffer })
      }
    })
  }

  send(data: ArrayBuffer | ArrayBufferView): void {
    if (this.readyState !== 'open') {
      return
    }
    const bytes = data instanceof ArrayBuffer ? new Uint8Array(data) : new Uint8Array(data.buffer, data.byteOffset, data.byteLength)
    this.client.send(new Uint8Array(bytes) as Uint8Array<ArrayBuffer>)
  }

  /** The browser closes it: the server closes the guest's end too. */
  close(): void {
    if (this.readyState === 'open') {
      void this.client.request('console.close').catch(() => {})
      this.end()
    }
  }

  /** The guest's end closed (`consoleClosed`), or the session did. */
  end(): void {
    if (this.readyState === 'closed') {
      return
    }
    this.readyState = 'closed'
    this.stop()
    this.onclose?.({ code: 1000, reason: '', wasClean: true })
  }
}
