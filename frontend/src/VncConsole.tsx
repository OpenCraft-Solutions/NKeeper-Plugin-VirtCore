import RFB from '@novnc/novnc'
import { useEffect, useRef, useState } from 'react'
import type { ToolClient, ToolMessage } from './client'
import { ConsoleChannel } from './console'

interface VncConsoleProps {
  client: ToolClient
  guest: string
  password: string | null
  onClose: (message: string | null) => void
}

/**
 * A guest's VNC console (ported from 1.x's VncConsole), with noVNC, over the guests session: scaled to the tab,
 * with Ctrl+Alt+Del. Closing it, or the guest ending it, goes back to the list.
 */
export function VncConsole({ client, guest, password, onClose }: VncConsoleProps) {
  const screenRef = useRef<HTMLDivElement>(null)
  const rfbRef = useRef<RFB | null>(null)
  const [connected, setConnected] = useState(false)
  const closeRef = useRef(onClose)
  useEffect(() => {
    closeRef.current = onClose
  })

  useEffect(() => {
    const screen = screenRef.current
    if (!screen) {
      return
    }
    const channel = new ConsoleChannel(client)
    let wasConnected = false
    let finished = false
    const finish = (message: string | null) => {
      if (!finished) {
        finished = true
        closeRef.current(message)
      }
    }
    const stopListening = client.on((message: ToolMessage) => {
      if (message.type === 'consoleClosed') {
        channel.end()
      }
    })
    let rfb: RFB
    try {
      rfb = new RFB(screen, channel as unknown as WebSocket, password ? { credentials: { username: '', password, target: '' } } : {})
    } catch (error) {
      channel.close()
      stopListening()
      finish(error instanceof Error ? `Couldn’t open the console: ${error.message}` : 'Couldn’t open the console.')
      return
    }
    rfb.scaleViewport = true
    rfb.background = '#000'
    rfbRef.current = rfb
    rfb.addEventListener('connect', () => {
      wasConnected = true
      setConnected(true)
    })
    rfb.addEventListener('credentialsrequired', () =>
      finish(
        password
          ? 'The virtual machine rejected the console password.'
          : 'This VM’s VNC display needs a password, and the hypervisor didn’t give it. The SSH user needs full (read-write) access to libvirt to read it.',
      ),
    )
    rfb.addEventListener('securityfailure', () => finish('The virtual machine rejected the console password.'))
    rfb.addEventListener('disconnect', () => {
      rfbRef.current = null
      finish(wasConnected ? 'The console closed. The virtual machine may have shut down or restarted.' : 'Couldn’t open the console.')
    })
    return () => {
      finished = true
      stopListening()
      rfbRef.current = null
      try {
        rfb.disconnect()
      } catch {
        // Already disconnected.
      }
      channel.close()
    }
  }, [client, guest, password])

  return (
    <div className="nk-virt-console">
      <div className="nk-virt-toolbar">
        <button type="button" className="nk-virt-button" onClick={() => onClose(null)}>
          ← Guests
        </button>
        <span className="nk-virt-muted">{connected ? `Console · ${guest}` : `Opening the console of ${guest}…`}</span>
        <button type="button" className="nk-virt-button" disabled={!connected} onClick={() => rfbRef.current?.sendCtrlAltDel()}>
          Ctrl+Alt+Del
        </button>
      </div>
      <div className="nk-virt-screen" ref={screenRef} />
    </div>
  )
}
