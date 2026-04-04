export function MethodTag({ method }: { method: string }) {
  return <span className={`method method-${method.toLowerCase()}`}>{method}</span>
}
