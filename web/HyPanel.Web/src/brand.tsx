/**
 * HyPanel brand: a rounded tile with an italic "Hy" drawn as mixing-console faders, plus an italic "Panel"
 * wordmark. The tile carries the only brand colour in the UI; the wordmark follows the text colour so it works
 * in light and dark themes.
 */

const BLUE = '#38bdf8'
const KNOB = '#e2e8f0'
const TILE = '#1c1e28'

/** Detail level: `full` for large lockups, `compact` for 20–48px, `tiny` for favicons (≤ 20px). */
export type BrandDetail = 'full' | 'compact' | 'tiny'

export function BrandMark({ size = 28, detail = 'compact', title }: { size?: number; detail?: BrandDetail; title?: string }) {
  return <svg className="brand-mark-svg" width={size} height={size} viewBox="0 0 260 260" role={title ? 'img' : undefined}
    aria-hidden={title ? undefined : true} aria-label={title}>
    <rect width="260" height="260" rx="60" fill={TILE} />
    {detail === 'full' && <rect width="256" height="256" x="2" y="2" rx="58" fill="none" stroke="rgba(255,255,255,0.08)" stroke-width="2" />}
    <g transform="skewX(-10) translate(36, 10)">
      <rect x="30" y="55" width="22" height="130" rx="6" fill={BLUE} />
      <rect x="50" y="111" width="46" height="15" fill={BLUE} />
      <rect x="94" y="55" width="22" height="130" rx="6" fill={BLUE} />
      {/* Fader knobs: the "panel" in the mark. */}
      <rect x="25" y="105" width="32" height="26" rx="4" fill={KNOB} />
      <rect x="89" y="75" width="32" height="26" rx="4" fill={KNOB} />
      {detail === 'full' && <>
        <rect x="31" y="116" width="20" height="3" rx="1.5" fill={TILE} />
        <rect x="95" y="86" width="20" height="3" rx="1.5" fill={TILE} />
      </>}
      <path d="M 132 94 L 154 94 L 172 140 L 157 140 Z" fill={BLUE} />
      {detail !== 'tiny' && <g transform="translate(160, 92)">
        <rect x="0" y="0" width="28" height="6" rx="3" fill={KNOB} />
        <rect x="4" y="9" width="24" height="6" rx="3" fill={BLUE} />
        {detail === 'full' && <rect x="10" y="18" width="18" height="6" rx="3" fill="#334155" />}
      </g>}
      <path d="M 186 94 L 208 94 L 168 190 L 152 222 L 130 222 L 148 190 L 163 158 Z" fill={BLUE} />
    </g>
  </svg>
}

/** "Panel" wordmark in the current text colour; `height` is the rendered cap height box. */
export function Wordmark({ height = 16 }: { height?: number }) {
  // Glyph box after the -10° skew spans roughly x −32…540, y 28…176 in the source coordinates.
  const width = Math.round(height * 572 / 148)
  return <svg className="brand-wordmark" width={width} height={height} viewBox="-32 28 572 148" aria-hidden="true">
    <g transform="skewX(-10)" fill="currentColor">
      <path d="M 0 170 L 32 170 L 32 104 L 75 104 C 95 104 108 92 108 72 C 108 52 95 40 75 40 L 0 40 Z M 32 80 L 32 64 L 70 64 C 77 64 81 66 81 72 C 81 78 77 80 70 80 Z" />
      <path d="M 175 170 L 175 150 C 168 165 152 173 134 173 C 114 173 98 160 98 140 C 98 116 120 108 152 108 L 175 108 L 175 102 C 175 92 168 87 154 87 C 142 87 132 91 120 96 L 112 76 C 126 70 142 66 160 66 C 190 66 206 80 206 108 L 206 170 Z M 175 125 L 156 125 C 138 125 129 130 129 140 C 129 148 136 154 146 154 C 162 154 175 144 175 129 Z" />
      <path d="M 230 170 L 230 70 L 258 70 L 258 87 C 267 75 281 67 298 67 C 322 67 338 80 338 108 L 338 170 L 308 170 L 308 114 C 308 100 300 93 288 93 C 274 93 260 103 260 120 L 260 170 Z" />
      <path d="M 408 173 C 374 173 352 150 352 120 C 352 88 376 67 406 67 C 438 67 455 90 455 122 L 455 128 L 382 128 C 384 144 394 153 410 153 C 422 153 434 148 444 141 L 454 161 C 441 169 425 173 408 173 Z M 406 87 C 394 87 385 95 383 110 L 426 110 C 425 96 418 87 406 87 Z" />
      <path d="M 474 170 L 474 30 L 504 30 L 504 170 Z" />
      <circle cx="530" cy="164" r="7" fill={BLUE} />
    </g>
  </svg>
}

/** Mark + wordmark, reading "Hy" + "Panel". */
export function BrandLockup({ size = 28 }: { size?: number }) {
  return <span className="brand-lockup" aria-label="HyPanel" role="img">
    <BrandMark size={size} detail={size >= 56 ? 'full' : 'compact'} />
    <Wordmark height={Math.round(size * 0.58)} />
  </span>
}
