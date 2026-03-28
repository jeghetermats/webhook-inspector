import { describe, expect, it } from 'vitest'
import { base64ToBytes, decodeBody, hexRows } from './body'

const utf8 = (text: string) => new TextEncoder().encode(text)

describe('decodeBody', () => {
  it('detects an empty body', () => {
    expect(decodeBody(new Uint8Array(), 'application/json').kind).toBe('empty')
  })

  it('detects JSON from the Content-Type and pretty-prints it', () => {
    const body = decodeBody(utf8('{"a":1,"b":[true]}'), 'application/json; charset=utf-8')
    expect(body.kind).toBe('json')
    expect(body.prettyJson).toBe('{\n  "a": 1,\n  "b": [\n    true\n  ]\n}')
  })

  it('detects JSON from the content when the Content-Type is wrong', () => {
    expect(decodeBody(utf8('  [1, 2]'), 'text/plain').kind).toBe('json')
    expect(decodeBody(utf8('{"x":"y"}'), null).kind).toBe('json')
  })

  it('treats invalid JSON as text even with a JSON Content-Type', () => {
    const body = decodeBody(utf8('{not json'), 'application/json')
    expect(body.kind).toBe('text')
    expect(body.text).toBe('{not json')
  })

  it('keeps form bodies and non-ASCII text as text', () => {
    expect(decodeBody(utf8('a=1&b=%C3%A6'), 'application/x-www-form-urlencoded').kind).toBe('text')
    const norwegian = decodeBody(utf8('blåbær\r\n\tsyltetøy'), 'text/plain')
    expect(norwegian.kind).toBe('text')
    expect(norwegian.text).toBe('blåbær\r\n\tsyltetøy')
  })

  it('treats invalid UTF-8 and control characters as binary', () => {
    expect(decodeBody(new Uint8Array([0xff, 0xfe, 0x00]), null).kind).toBe('binary')
    expect(decodeBody(new Uint8Array([0x48, 0x00, 0x49]), 'text/plain').kind).toBe('binary')
    expect(decodeBody(utf8('bell\u0007'), null).kind).toBe('binary')
  })
})

describe('hexRows', () => {
  it('formats offset, hex and printable ASCII, 16 bytes per row', () => {
    const bytes = new Uint8Array([...utf8('Hello, webhook!!'), 0x00, 0xff])
    expect(hexRows(bytes)).toEqual([
      {
        offset: '00000000',
        hex: '48 65 6c 6c 6f 2c 20 77 65 62 68 6f 6f 6b 21 21',
        ascii: 'Hello, webhook!!',
      },
      { offset: '00000010', hex: '00 ff', ascii: '..' },
    ])
  })

  it('stops at the byte limit', () => {
    expect(hexRows(new Uint8Array(100), 32)).toHaveLength(2)
  })
})

describe('base64ToBytes', () => {
  it('round-trips binary data', () => {
    expect(Array.from(base64ToBytes('AAH/'))).toEqual([0x00, 0x01, 0xff])
  })
})
