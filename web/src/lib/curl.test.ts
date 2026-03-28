import { describe, expect, it } from 'vitest'
import { shellQuote, toCurl } from './curl'

const base = { id: 'abc', method: 'POST', headers: [], bodySize: 0 }

describe('shellQuote', () => {
  it('wraps in single quotes and escapes embedded single quotes', () => {
    expect(shellQuote("it's")).toBe(`'it'\\''s'`)
  })

  it('leaves shell metacharacters inert', () => {
    expect(shellQuote('$(rm -rf ~) `id` "x" \\ ; & | > *')).toBe(`'$(rm -rf ~) \`id\` "x" \\ ; & | > *'`)
  })
})

describe('toCurl', () => {
  it('builds a POST with headers and a text body', () => {
    const command = toCurl(
      {
        ...base,
        headers: [
          { name: 'Content-Type', value: 'application/json' },
          { name: 'X-Hub-Signature-256', value: 'sha256=abc' },
        ],
        bodySize: 15,
      },
      { url: 'https://example.test/h/tok/push?a=1&a=2', bodyText: `{"msg":"it's"}` },
    )

    expect(command).toBe(
      [
        'curl',
        '-X POST',
        `'https://example.test/h/tok/push?a=1&a=2'`,
        `-H 'Content-Type: application/json'`,
        `-H 'X-Hub-Signature-256: sha256=abc'`,
        `--data-binary '{"msg":"it'\\''s"}'`,
      ].join(' \\\n  '),
    )
  })

  it('keeps repeated headers as separate -H flags', () => {
    const command = toCurl(
      { ...base, method: 'GET', headers: [{ name: 'X-Multi', value: 'one' }, { name: 'X-Multi', value: 'two' }] },
      { url: 'https://e.test/', bodyText: null },
    )
    expect(command).toContain(`-H 'X-Multi: one' \\\n  -H 'X-Multi: two'`)
  })

  it('drops headers curl sets itself and headers added by the proxy', () => {
    const command = toCurl(
      {
        ...base,
        headers: [
          { name: 'Host', value: 'example.test' },
          { name: 'Content-Length', value: '3' },
          { name: 'Accept-Encoding', value: 'gzip' },
          { name: 'X-Forwarded-For', value: '1.2.3.4' },
          { name: 'x-envoy-external-address', value: '1.2.3.4' },
          { name: 'User-Agent', value: 'GitHub-Hookshot/abc' },
        ],
      },
      { url: 'https://e.test/', bodyText: null },
    )
    expect(command).not.toMatch(/Host:|Content-Length|Accept-Encoding|X-Forwarded|x-envoy/)
    expect(command).toContain(`-H 'User-Agent: GitHub-Hookshot/abc'`)
  })

  it('omits -X for a plain GET and uses --head for HEAD', () => {
    expect(toCurl({ ...base, method: 'GET' }, { url: 'https://e.test/', bodyText: null })).toBe(`curl \\\n  'https://e.test/'`)
    expect(toCurl({ ...base, method: 'HEAD' }, { url: 'https://e.test/', bodyText: null })).toBe(`curl \\\n  --head \\\n  'https://e.test/'`)
  })

  it('points binary bodies at the downloaded file', () => {
    const command = toCurl({ ...base, bodySize: 4 }, { url: 'https://e.test/', bodyText: null })
    expect(command).toContain('--data-binary @request-abc.bin')
  })
})
