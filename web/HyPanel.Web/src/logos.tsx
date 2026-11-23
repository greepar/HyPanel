// Project marks, drawn from each project's own media kit so they follow the theme through currentColor.
// Hysteria: apernet/hysteria media-kit "symbol 1". Xray: XTLS/Xray-docs-next public/logo.

function HysteriaLogo() {
  return (
    <svg viewBox="0 0 442.19 323.31" aria-hidden="true">
      <g fill="currentColor">
        <polygon points="72.8 140.45 60.1 285.55 102.51 285.55 111.5 182.86 111.54 182.86 115.21 140.45 72.8 140.45" />
        <polygon points="124.16 37.75 81.76 37.75 76.19 101.36 118.59 101.36 124.16 37.75" />
        <polygon points="318.36 285.56 360.44 285.56 366.01 221.95 323.9 221.95 318.36 285.56" />
        <polygon points="382.09 37.76 340 37.76 329.16 161.66 221.09 161.66 206.95 323.31 292.78 201.84 438.67 201.84 442.19 161.66 371.25 161.66 382.09 37.76" />
      </g>
      <polygon fill="#ffbc00" points="149.41 121.47 3.52 121.47 0 161.66 221.09 161.66 235.23 0 149.41 121.47" />
    </svg>
  );
}

function XrayLogo() {
  return (
    <svg viewBox="0 0 1000 1000" aria-hidden="true">
      <g fill="currentColor">
        <polygon points="530,530 900,530 650,650 530,1000" />
        <polygon points="470,530 470,900 350,650 0,530" />
        <polygon points="530,470 530,100 650,350 1000,470" />
        <polygon points="470,470 100,470 350,350 470,0" />
      </g>
    </svg>
  );
}

/** The protocol's mark (by the server's badge), falling back to the badge text for a backend without one. */
export function BackendMark({ badge }: { badge: string }) {
  const logo = badge === "HY2" ? <HysteriaLogo /> : badge === "XR" ? <XrayLogo /> : null;
  return <span className={logo ? "backend-mark has-logo" : "backend-mark"}>{logo ?? badge}</span>;
}
