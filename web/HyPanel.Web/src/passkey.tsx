import { useEffect, useState } from 'preact/hooks'
import type { ApiClient } from './api'
import type { User } from './domain'
import { formatRelative, messageFor } from './ui'

/** Options from the Server; binary values are base64url. */
type PasskeyOptions = { challengeId: string; challenge: string; rpId: string; rpName: string; userHandle: string | null; userName: string | null; excludeCredentialIds: string[] }
export type Passkey = { id: string; name: string; createdAtUtc: string; lastUsedAtUtc: string | null }

const ES256 = -7
const RS256 = -257

const toBytes = (value: string) => {
  const base64 = value.replace(/-/g, '+').replace(/_/g, '/').padEnd(Math.ceil(value.length / 4) * 4, '=')
  return Uint8Array.from(atob(base64), char => char.charCodeAt(0))
}
const fromBytes = (value: ArrayBuffer) => {
  let text = ''
  for (const byte of new Uint8Array(value)) text += String.fromCharCode(byte)
  return btoa(text).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
}

export const passkeySupported = () => typeof window !== 'undefined' && !!window.PublicKeyCredential && !!navigator.credentials

/** A cancelled or timed-out browser prompt is not an error worth showing. */
const cancelled = (reason: unknown) => reason instanceof DOMException && (reason.name === 'NotAllowedError' || reason.name === 'AbortError')

/** Passwordless sign-in with a discoverable credential; returns null when the user dismisses the prompt. */
export async function signInWithPasskey(api: ApiClient): Promise<{ token: string; expiresAtUtc: string; user: User } | null> {
  const options = await api.request<PasskeyOptions>('/api/auth/v1/passkey/options', { method: 'POST' })
  let credential: PublicKeyCredential
  try {
    credential = await navigator.credentials.get({ publicKey: { challenge: toBytes(options.challenge), rpId: options.rpId, userVerification: 'preferred', timeout: 120000 } }) as PublicKeyCredential
  } catch (reason) {
    if (cancelled(reason)) return null
    throw reason
  }
  const response = credential.response as AuthenticatorAssertionResponse
  return api.request('/api/auth/v1/passkey/login', {
    method: 'POST',
    body: JSON.stringify({
      challengeId: options.challengeId,
      credentialId: fromBytes(credential.rawId),
      clientDataJson: fromBytes(response.clientDataJSON),
      authenticatorData: fromBytes(response.authenticatorData),
      signature: fromBytes(response.signature),
      userHandle: response.userHandle ? fromBytes(response.userHandle) : null,
    }),
  })
}

async function registerPasskey(api: ApiClient, name: string): Promise<Passkey | null> {
  const options = await api.request<PasskeyOptions>('/api/user/v1/passkeys/options', { method: 'POST' })
  let credential: PublicKeyCredential
  try {
    credential = await navigator.credentials.create({
      publicKey: {
        challenge: toBytes(options.challenge),
        rp: { id: options.rpId, name: options.rpName },
        user: { id: toBytes(options.userHandle ?? ''), name: options.userName ?? '', displayName: options.userName ?? '' },
        pubKeyCredParams: [{ type: 'public-key', alg: ES256 }, { type: 'public-key', alg: RS256 }],
        excludeCredentials: options.excludeCredentialIds.map(id => ({ type: 'public-key' as const, id: toBytes(id) })),
        authenticatorSelection: { residentKey: 'required', requireResidentKey: true, userVerification: 'preferred' },
        attestation: 'none',
        timeout: 120000,
      },
    }) as PublicKeyCredential
  } catch (reason) {
    if (cancelled(reason)) return null
    if (reason instanceof DOMException && reason.name === 'InvalidStateError') throw new Error('这个设备上已经保存过此账户的通行密钥。')
    throw reason
  }
  const response = credential.response as AuthenticatorAttestationResponse
  const publicKey = response.getPublicKey?.()
  const algorithm = response.getPublicKeyAlgorithm?.()
  if (!publicKey || algorithm === undefined) throw new Error('浏览器没有返回公钥，请升级浏览器后重试。')
  return api.request<Passkey>('/api/user/v1/passkeys', {
    method: 'POST',
    body: JSON.stringify({
      challengeId: options.challengeId,
      name,
      credentialId: fromBytes(credential.rawId),
      clientDataJson: fromBytes(response.clientDataJSON),
      authenticatorData: fromBytes(response.getAuthenticatorData()),
      publicKey: fromBytes(publicKey),
      algorithm,
    }),
  })
}

const defaultName = () => {
  const agent = navigator.userAgent
  const platform = /iPhone|iPad/.test(agent) ? 'iPhone / iPad' : /Android/.test(agent) ? 'Android' : /Mac/.test(agent) ? 'Mac' : /Windows/.test(agent) ? 'Windows' : /Linux/.test(agent) ? 'Linux' : '设备'
  return `${platform} 通行密钥`
}

/** Lists, adds and removes the signed-in account's passkeys. */
export function PasskeyPanel({ api, setError }: { api: ApiClient; setError: (value: string) => void }) {
  const [passkeys, setPasskeys] = useState<Passkey[] | null>(null)
  const [busy, setBusy] = useState(false)
  const load = async () => {
    try { setPasskeys(await api.request<Passkey[]>('/api/user/v1/passkeys')) }
    catch (reason) { setError(messageFor(reason, '无法加载通行密钥')) }
  }
  useEffect(() => { void load() }, [])
  const add = async () => {
    const name = prompt('给这个通行密钥起个名字', defaultName())
    if (name === null) return
    setBusy(true)
    try {
      if (await registerPasskey(api, name.trim())) { setError('通行密钥已添加，下次可直接用它登录。'); await load() }
    } catch (reason) { setError(messageFor(reason, '无法添加通行密钥')) }
    finally { setBusy(false) }
  }
  const remove = async (passkey: Passkey) => {
    if (!confirm(`删除通行密钥“${passkey.name}”？删除后它将无法再登录此账户。`)) return
    try { await api.request<void>(`/api/user/v1/passkeys/${encodeURIComponent(passkey.id)}`, { method: 'DELETE' }); setError('通行密钥已删除。'); await load() }
    catch (reason) { setError(messageFor(reason, '无法删除通行密钥')) }
  }
  return <section className="card panel">
    <div className="section-heading">
      <div><h2>通行密钥</h2><p>用指纹、面容或设备 PIN 免密码登录。</p></div>
      <button className="button button-primary" type="button" disabled={busy || !passkeySupported()} onClick={() => void add()}>{busy ? '等待验证…' : '添加通行密钥'}</button>
    </div>
    {!passkeySupported() && <p className="muted">当前浏览器不支持通行密钥。</p>}
    {passkeys === null ? <p className="muted">加载中…</p> : passkeys.length ? <div className="backup-list">{passkeys.map(passkey => <article className="backup-row" key={passkey.id}>
      <div><strong>{passkey.name}</strong><small>添加于 {new Date(passkey.createdAtUtc).toLocaleDateString('zh-CN')} · {passkey.lastUsedAtUtc ? `上次使用 ${formatRelative(passkey.lastUsedAtUtc)}` : '尚未使用'}</small></div>
      <div className="row-actions"><button className="button button-secondary" type="button" onClick={() => void remove(passkey)}>删除</button></div>
    </article>)}</div> : <p className="muted">还没有通行密钥。</p>}
  </section>
}
