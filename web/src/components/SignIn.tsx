export function SignIn() {
  const params = new URLSearchParams(window.location.search)
  const failed = params.get('signin') === 'failed'
  params.delete('signin')
  const query = params.toString()
  const returnUrl = window.location.pathname + (query ? `?${query}` : '')
  const loginUrl = `/auth/login?returnUrl=${encodeURIComponent(returnUrl)}`

  return (
    <main className="signin">
      <div className="signin-card">
        <h1 className="signin-title">Webhook Inspector</h1>
        <p className="signin-lede">
          Get a URL, point a webhook at it, and read every request it receives: method, headers, query and body.
        </p>
        <pre className="wire signin-sample" aria-hidden="true">
          <span className="wire-request">
            <span className="method method-post">POST</span> /github/push
          </span>
          {'\n'}
          <span className="wire-name">Content-Type</span>: application/json{'\n'}
          <span className="wire-name">X-GitHub-Event</span>: push{'\n'}
          {'\n'}
          {'{ "ref": "refs/heads/main", "commits": 3 }'}
        </pre>
        {failed ? (
          <p className="error" role="alert">
            Signing in with GitHub did not complete. Try again; if it keeps failing, the server log has the reason.
          </p>
        ) : null}
        <a className="button button-primary" href={loginUrl}>
          Sign in with GitHub
        </a>
        <p className="signin-note">
          Only your GitHub username and id are stored. Want to look around first? <a href="/demo">Try the demo</a> with
          made-up data.
        </p>
      </div>
    </main>
  )
}
