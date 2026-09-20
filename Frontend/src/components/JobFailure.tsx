// What a failed crawl or check says about itself: the sanitized reason the API
// returns, the stage it stopped at, and a link to the workflow run that handled
// it (which is where the full logs are).
const stageLabels: Record<string, string> = {
  dispatch: 'starting the workflow',
  budget: 'queueing (too many recent requests)',
  start: 'starting up',
  'launch-browser': 'launching the browser',
  scrape: 'reading the page',
  persist: 'saving the result',
  cancelled: 'being cancelled',
  timeout: 'waiting for a worker',
  result: 'reading the stored result',
  migration: 'a database upgrade',
  workflow: 'the workflow (before the crawler could report)',
}

export default function JobFailure({
  prefix,
  message,
  stage,
  runUrl,
}: {
  prefix: string
  message: string | null
  stage: string | null
  runUrl: string | null
}) {
  return (
    <div role="alert" className="text-sm text-flag-red">
      <p>
        {prefix}: {message ?? 'no reason was recorded.'}
      </p>
      {(stage || runUrl) && (
        <p className="mt-1 text-xs text-ink-600">
          {stage && <span>stopped while {stageLabels[stage] ?? stage}</span>}
          {stage && runUrl && ' · '}
          {runUrl && (
            <a href={runUrl} target="_blank" rel="noreferrer" className="underline">
              view the workflow run
            </a>
          )}
        </p>
      )}
    </div>
  )
}
