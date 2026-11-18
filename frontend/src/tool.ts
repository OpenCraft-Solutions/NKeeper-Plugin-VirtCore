import { useEffect, useState } from 'react'
import { SessionError, type SessionViewProps } from '@nkeeper/sdk'
import { ToolClient, type ToolMessage } from './client'

/** The tool's lifecycle: opening, open with its client, or ended (why, and whether it failed to open). */
export type ToolState = { kind: 'connecting' } | { kind: 'open'; client: ToolClient } | { kind: 'ended'; message: string; failed: boolean }

const END_MESSAGES: Record<string, string> = {
  closed: 'The session ended.',
  access_revoked: 'Your access to this host changed, so the session ended.',
  failed: 'The session failed.',
  disconnected: 'The connection to the server was lost.',
}

/** Opens the tool's session, and reopens it when `attempt` changes. A `fatal` message from the tool ends it with its reason. */
export function useTool(connect: SessionViewProps['connect'], what: string): { state: ToolState; reconnect: () => void } {
  const [state, setState] = useState<ToolState>({ kind: 'connecting' })
  const [attempt, setAttempt] = useState(0)

  useEffect(() => {
    let disposed = false
    let client: ToolClient | null = null
    const opening = new AbortController()
    connect({}, opening.signal).then(
      (connection) => {
        if (disposed) {
          connection.close()
          return
        }
        let fatal: string | null = null
        client = new ToolClient(connection)
        client.on((message: ToolMessage) => {
          if (message.type === 'fatal' && !disposed) {
            fatal = typeof message.message === 'string' ? message.message : 'The tool stopped.'
            setState({ kind: 'ended', message: fatal, failed: true })
          }
        })
        setState({ kind: 'open', client })
        void connection.closed.then((reason) => {
          if (!disposed && fatal === null) {
            setState({ kind: 'ended', message: END_MESSAGES[reason] ?? END_MESSAGES.closed!, failed: false })
          }
        })
      },
      (error: unknown) => {
        if (!disposed) {
          setState({ kind: 'ended', message: error instanceof SessionError ? error.message : `${what} couldn't be opened.`, failed: true })
        }
      },
    )
    return () => {
      disposed = true
      opening.abort()
      client?.close()
    }
  }, [connect, attempt, what])

  return {
    state,
    reconnect: () => {
      setState({ kind: 'connecting' })
      setAttempt((n) => n + 1)
    },
  }
}
