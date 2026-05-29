import { type ReactNode, useId, useRef } from 'react'

interface ConfirmButtonProps {
  /** The button that opens the dialog. */
  children: ReactNode
  className?: string
  disabled?: boolean
  /** Names the action and the object, e.g. Delete “Q3 report”? */
  title: string
  /** What happens, and whether it can be undone. */
  body: ReactNode
  /** Verb plus object, e.g. "Delete endpoint". Never "OK". */
  action: string
  destructive?: boolean
  onConfirm: () => void
}

/**
 * A button that asks before acting, in a native modal <dialog>: the page behind is inert,
 * Escape cancels, and the browser returns focus to the button when the dialog closes.
 * Cancel comes first, so showModal() focuses it and Enter never destroys anything by accident.
 */
export function ConfirmButton({ children, className = 'button', disabled, title, body, action, destructive, onConfirm }: ConfirmButtonProps) {
  const dialog = useRef<HTMLDialogElement>(null)
  const titleId = useId()
  const bodyId = useId()

  return (
    <>
      <button type="button" className={className} disabled={disabled} onClick={() => dialog.current?.showModal()}>
        {children}
      </button>
      <dialog
        ref={dialog}
        className="dialog"
        aria-labelledby={titleId}
        aria-describedby={bodyId}
        onClose={(event) => {
          if (event.currentTarget.returnValue === 'confirm') onConfirm()
          event.currentTarget.returnValue = ''
        }}
      >
        <form method="dialog" className="dialog-form">
          <h2 id={titleId} className="dialog-title">
            {title}
          </h2>
          <div id={bodyId} className="dialog-body">
            {body}
          </div>
          <div className="dialog-actions">
            <button type="submit" value="cancel" className="button">
              Cancel
            </button>
            <button type="submit" value="confirm" className={destructive ? 'button button-danger-solid' : 'button button-primary'}>
              {action}
            </button>
          </div>
        </form>
      </dialog>
    </>
  )
}
