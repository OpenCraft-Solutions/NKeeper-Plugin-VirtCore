import { useCallback, useEffect, useState } from 'react'
import type { SessionViewProps } from '@nkeeper/sdk'
import { ToolError, type ToolClient } from './client'
import { useTool } from './tool'
import { VncConsole } from './VncConsole'

/** What a hypervisor plugin tells the tab about itself. */
export interface HypervisorInfo {
  /** For headings and messages: "KVM". */
  readonly product: string
}

/** What a hypervisor's view gives virt-core's: the session view's own props, and the hypervisor. */
export interface GuestsViewProps extends SessionViewProps {
  hypervisor: HypervisorInfo
}

interface Guest {
  id: string
  name: string
  state: string
  console: 'vnc' | 'spice' | null
  actions: string[]
}

/** The power actions, as 1.x labelled them, and whether each cuts the guest off, so the tab asks first. */
const ACTIONS: Record<string, { label: string; ask?: string }> = {
  start: { label: 'Start' },
  shutdown: { label: 'Shut down', ask: 'shuts it down: its programs are asked to close' },
  reboot: { label: 'Reboot', ask: 'restarts it' },
  pause: { label: 'Pause', ask: 'freezes it until it’s resumed' },
  resume: { label: 'Resume' },
  hibernate: { label: 'Hibernate', ask: 'saves its memory to disk and turns it off' },
  stop: { label: 'Force off', ask: 'turns it off at once, as pulling the plug does: unsaved work is lost' },
  reset: { label: 'Reset', ask: 'restarts it at once, as the reset button does: unsaved work is lost' },
}

const message = (error: unknown, fallback: string) => (error instanceof ToolError ? error.message : fallback)

/**
 * A hypervisor's guests (docs/architecture.md §7.36, ported from 1.x's VM list): each with its state, its power actions
 * and its console, which opens in the same tab.
 */
export function GuestsView({ connect, host, visible, hypervisor }: GuestsViewProps) {
  const { state, reconnect } = useTool(connect, hypervisor.product)
  return (
    <div className="nk-virt" hidden={!visible} role="region" aria-label={`${host.name} ${hypervisor.product} guests`}>
      <div className="nk-virt-status">
        <span className="nk-virt-dot" data-state={state.kind} />
        <strong>{state.kind === 'connecting' ? 'Connecting…' : state.kind === 'open' ? `${hypervisor.product} guests` : state.failed ? "Couldn't connect" : 'Ended'}</strong>
        <span className="nk-virt-muted">{host.name}</span>
        {state.kind === 'ended' ? (
          <span className="nk-virt-actions">
            {!state.failed ? <span className="nk-virt-muted">{state.message}</span> : null}
            <button type="button" className="nk-virt-button" onClick={reconnect}>
              Reconnect
            </button>
          </span>
        ) : null}
      </div>
      <div className="nk-virt-body">
        {state.kind === 'connecting' ? <p className="nk-virt-muted">Connecting to {host.name}…</p> : null}
        {state.kind === 'ended' && state.failed ? (
          <p className="nk-virt-error" role="alert">
            {state.message}
          </p>
        ) : null}
        {state.kind === 'open' ? <Guests client={state.client} hypervisor={hypervisor} /> : null}
      </div>
    </div>
  )
}

function Guests({ client, hypervisor }: { client: ToolClient; hypervisor: HypervisorInfo }) {
  const [guests, setGuests] = useState<Guest[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [asking, setAsking] = useState<{ guest: Guest; action: string } | null>(null)
  const [console, setConsole] = useState<{ guest: Guest; password: string | null } | null>(null)

  const refresh = useCallback(async () => {
    try {
      setGuests((await client.request<{ guests: Guest[] }>('list')).guests)
      setError(null)
    } catch (e) {
      setError(message(e, `Couldn’t list the guests.`))
    }
  }, [client])

  useEffect(() => {
    const timer = window.setTimeout(() => void refresh(), 0)
    return () => window.clearTimeout(timer)
  }, [refresh])

  async function power(guest: Guest, action: string) {
    setAsking(null)
    setBusy(guest.id)
    setNotice(null)
    setError(null)
    try {
      await client.request('power', { guest: guest.id, action })
      setNotice(`${ACTIONS[action]?.label ?? action}: ${guest.name}.`)
      await refresh()
    } catch (e) {
      setError(message(e, `Couldn’t ${action} ${guest.name}.`))
    } finally {
      setBusy(null)
    }
  }

  async function openConsole(guest: Guest) {
    setNotice(null)
    setError(null)
    try {
      const opened = await client.request<{ protocol: string; password: string | null }>('console', { guest: guest.id })
      if (opened.protocol !== 'vnc') {
        void client.request('console.close').catch(() => {})
        setError(`${guest.name}’s console is ${opened.protocol.toUpperCase()}, which this tab can’t show yet.`)
        return
      }
      setConsole({ guest, password: opened.password })
    } catch (e) {
      setError(message(e, `Couldn’t open ${guest.name}’s console.`))
    }
  }

  if (console) {
    return (
      <VncConsole
        client={client}
        guest={console.guest.name}
        password={console.password}
        onClose={(why) => {
          setConsole(null)
          if (why) {
            setError(why)
          }
          void refresh()
        }}
      />
    )
  }

  return (
    <>
      <div className="nk-virt-toolbar">
        <button type="button" className="nk-virt-button" onClick={() => void refresh()}>
          Refresh
        </button>
        {guests ? <span className="nk-virt-muted">{guests.length === 1 ? '1 guest' : `${guests.length} guests`}</span> : null}
      </div>
      {notice ? (
        <p className="nk-virt-notice" role="status">
          {notice}
        </p>
      ) : null}
      {error ? (
        <p className="nk-virt-error" role="alert">
          {error}
        </p>
      ) : null}
      {asking ? (
        <div className="nk-virt-notice" role="alertdialog" aria-label={`${ACTIONS[asking.action]?.label} ${asking.guest.name}?`}>
          <span>
            {ACTIONS[asking.action]?.label} {asking.guest.name}? This {ACTIONS[asking.action]?.ask}.
          </span>
          <button type="button" className="nk-virt-button" data-danger="" onClick={() => void power(asking.guest, asking.action)}>
            {ACTIONS[asking.action]?.label}
          </button>
          <button type="button" className="nk-virt-button" onClick={() => setAsking(null)}>
            Cancel
          </button>
        </div>
      ) : null}
      {guests && guests.length === 0 ? <p className="nk-virt-muted">{hypervisor.product} has no guests.</p> : null}
      {guests && guests.length > 0 ? (
        <table className="nk-virt-table" aria-label="Guests">
          <thead>
            <tr>
              <th scope="col">Name</th>
              <th scope="col">State</th>
              <th scope="col">Console</th>
              <th scope="col">Actions</th>
            </tr>
          </thead>
          <tbody>
            {guests.map((guest) => (
              <tr key={guest.id}>
                <td>{guest.name}</td>
                <td>
                  <span className="nk-virt-state" data-running={guest.state === 'running' ? '' : undefined}>
                    {guest.state}
                  </span>
                </td>
                <td>
                  {guest.console ? (
                    <button type="button" className="nk-virt-button" disabled={guest.state !== 'running'} onClick={() => void openConsole(guest)}>
                      {guest.console.toUpperCase()}
                    </button>
                  ) : (
                    <span className="nk-virt-muted">—</span>
                  )}
                </td>
                <td className="nk-virt-row-actions">
                  {guest.actions.map((action) => (
                    <button
                      key={action}
                      type="button"
                      className="nk-virt-button"
                      data-danger={action === 'stop' || action === 'reset' ? '' : undefined}
                      disabled={busy === guest.id}
                      onClick={() => (ACTIONS[action]?.ask ? setAsking({ guest, action }) : void power(guest, action))}
                    >
                      {ACTIONS[action]?.label ?? action}
                    </button>
                  ))}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      ) : null}
    </>
  )
}
