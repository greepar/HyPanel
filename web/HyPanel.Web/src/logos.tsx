// Protocol marks, drawn so they follow the theme through currentColor.
// Hysteria: apernet/hysteria media-kit "symbol 1". Xray: XTLS/Xray-docs-next public/logo.
// Shadowsocks: the paper plane of shadowsocks-android (core/res/drawable/ic_service_active).
// AnyTLS has no logo of its own; ours is a padlock (TLS) holding uneven bars (its padding).

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

function ShadowsocksLogo() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true">
      <path fill="currentColor" d="M21.25 2.28 17.55 18.55 9.26 15.89 16.58 7.16 6.83 15.37 0 12.8 21.25 2.28ZM9.45 17.56l2.64.85L9.46 22l-.01-4.44Z" />
    </svg>
  );
}

function AnyTlsLogo() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true">
      <path d="M8 11V7.5a4 4 0 0 1 8 0V11" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" />
      <rect x="4" y="10.5" width="16" height="11.5" rx="2.6" fill="currentColor" />
      <g stroke="var(--surface-3)" stroke-width="1.7" stroke-linecap="round">
        <path d="M7.5 14h9" />
        <path d="M7.5 16.5h5" />
        <path d="M7.5 19h7" />
      </g>
    </svg>
  );
}

const logos: Record<string, () => preact.JSX.Element> = {
  HY2: HysteriaLogo,
  XR: XrayLogo,
  SS: ShadowsocksLogo,
  AT: AnyTlsLogo,
};

/** The protocol's mark (by the server's badge), falling back to the badge text for a backend without one. */
export function BackendMark({ badge }: { badge: string }) {
  const Logo = logos[badge];
  const logo = Logo ? <Logo /> : null;
  return <span className={logo ? "backend-mark has-logo" : "backend-mark"}>{logo ?? badge}</span>;
}
