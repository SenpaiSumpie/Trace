import { useState, type FormEvent } from 'react'
import { createTask } from './api'

export function NewTaskForm({ onCreated }: { onCreated: () => void }) {
  const [title, setTitle] = useState('')
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setSaving(true)
    setError(null)
    try {
      await createTask(title)
      setTitle('')
      onCreated()
    } catch (reason) {
      // Keep what was typed so the user can try again.
      setError((reason as Error).message)
    } finally {
      setSaving(false)
    }
  }

  return (
    <form onSubmit={submit}>
      <label htmlFor="new-task-title">New task</label>
      <input id="new-task-title" value={title} onChange={(event) => setTitle(event.target.value)} />
      <button type="submit" disabled={saving || title.trim().length === 0}>
        {saving ? 'Adding…' : 'Add task'}
      </button>
      {error && <p role="alert">Could not add the task: {error}</p>}
    </form>
  )
}
