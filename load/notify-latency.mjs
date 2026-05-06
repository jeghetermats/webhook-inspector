// Measures the time from sending a webhook to receiving its live notification over SignalR,
// the same path the browser uses (PLAN.md section 12). Run it while k6 adds load.
//
// Needs your session: copy the value of the __Host-wi-auth cookie from the browser's dev tools
// into an environment variable in your own terminal. Never paste it into chat or commit it.
//
//   $env:BASE_URL = 'https://...'; $env:ENDPOINT_ID = '...'; $env:TOKEN = '...'
//   $env:AUTH_COOKIE = '<value of __Host-wi-auth>'   (or put the value in load/.auth-cookie)
//   npm run notify-latency
//
// SAMPLES (default 60) requests are sent INTERVAL_MS (default 1000) apart.
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import { existsSync, readFileSync, writeFileSync } from 'node:fs'

const baseUrl = (process.env.BASE_URL ?? '').replace(/\/$/, '')
const endpointId = process.env.ENDPOINT_ID
const token = process.env.TOKEN
// AUTH_COOKIE, or the file load/.auth-cookie (git-ignored), so the value never has to be
// typed into a shared terminal or chat. Delete the file when you are done.
const cookieFile = new URL('./.auth-cookie', import.meta.url)
const rawCookie = process.env.AUTH_COOKIE || (existsSync(cookieFile) ? readFileSync(cookieFile, 'utf8') : '')
// Tolerate a pasted "__Host-wi-auth=..." or surrounding quotes.
const authCookie = rawCookie
  .trim()
  .replace(/^["']|["']$/g, '')
  .replace(/^__Host-wi-auth=/, '')
const samples = Number(process.env.SAMPLES ?? 60)
const intervalMs = Number(process.env.INTERVAL_MS ?? 1000)
const label = process.env.LABEL ?? 'notify-latency'
const cookieName = process.env.AUTH_COOKIE_NAME ?? '__Host-wi-auth'

for (const [name, value] of Object.entries({ BASE_URL: baseUrl, ENDPOINT_ID: endpointId, TOKEN: token, AUTH_COOKIE: authCookie })) {
  if (!value) throw new Error(`Set ${name}.`)
}

for (const [name, value] of Object.entries({ ENDPOINT_ID: endpointId, TOKEN: token, AUTH_COOKIE: authCookie })) {
  if (value.includes('<')) throw new Error(`${name} still contains a placeholder. Replace it with the real value.`)
}

// Check the session before starting SignalR, which would only say "401".
const me = await fetch(`${baseUrl}/api/me`, { headers: { Cookie: `${cookieName}=${authCookie}` } })
if (me.status !== 200) {
  throw new Error(
    `The server did not accept AUTH_COOKIE (status ${me.status}). Copy the whole Value of ${cookieName} ` +
      `(it starts with CfDJ8 and is several hundred characters long) from the browser's dev tools, ` +
      `while signed in on ${baseUrl}. You have ${authCookie.length} characters now.`,
  )
}
console.log(`Signed in as ${(await me.json()).login}.`)

const sentAt = new Map()
const latencies = []

const connection = new HubConnectionBuilder()
  .withUrl(`${baseUrl}/hubs/requests`, { headers: { Cookie: `${cookieName}=${authCookie}` } })
  .configureLogging(LogLevel.Warning)
  .build()

connection.on('RequestReceived', (event) => {
  const match = /^\/latency\/(\d+)$/.exec(event.path)
  if (!match || !sentAt.has(match[1])) return
  latencies.push(performance.now() - sentAt.get(match[1]))
  sentAt.delete(match[1])
})

await connection.start()
await connection.invoke('Subscribe', endpointId)
console.log(`Connected and subscribed. Sending ${samples} requests, ${intervalMs} ms apart...`)

let rejected = 0
for (let i = 0; i < samples; i++) {
  const id = String(i)
  sentAt.set(id, performance.now())
  const response = await fetch(`${baseUrl}/h/${token}/latency/${id}`, { method: 'POST', body: '{}', headers: { 'Content-Type': 'application/json' } })
  if (response.status !== 200) {
    sentAt.delete(id)
    rejected++
  }
  await new Promise((resolve) => setTimeout(resolve, intervalMs))
}

await new Promise((resolve) => setTimeout(resolve, 5000)) // let the last notifications arrive
await connection.stop()

const sorted = [...latencies].sort((a, b) => a - b)
const pct = (p) => (sorted.length ? sorted[Math.min(sorted.length - 1, Math.ceil((p / 100) * sorted.length) - 1)] : NaN)
const result = {
  label,
  sent: samples,
  rejected,
  notified: latencies.length,
  missing: samples - rejected - latencies.length,
  sendToNotificationMs: { median: pct(50), p95: pct(95), max: sorted.at(-1) },
}
writeFileSync(new URL(`./results/${label}.json`, import.meta.url), JSON.stringify(result, null, 2))
console.log(
  `${label}: ${result.notified} of ${samples - rejected} notified (${result.missing} missing, ${rejected} rejected); ` +
    `send to notification ms: median ${result.sendToNotificationMs.median?.toFixed(0)}, ` +
    `p95 ${result.sendToNotificationMs.p95?.toFixed(0)}, max ${result.sendToNotificationMs.max?.toFixed(0)}`,
)
