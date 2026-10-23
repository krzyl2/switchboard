export const en = {
  loading: 'Loading status…',
  rowMode: 'Mode',
  rowHomeAssistant: 'Home Assistant',
  rowMqtt: 'MQTT',
  rowHealth: 'Health',
  rowVersion: 'Version',
  modeShadow: 'Shadow',
  modeShadowNote: 'Computes and logs, never switches.',
  modeActive: 'Active',
  modeActiveNote: 'This build has no command path and cannot switch anything.',
  connectionConnected: 'Connected',
  connectionConnecting: 'Connecting',
  connectionDisconnected: 'Disconnected',
  healthHealthy: 'Healthy',
  healthUnhealthy: 'Unhealthy',
  lastUpdated: 'Last updated',
  unavailable: 'Status unavailable',
  lastSuccessfulUpdate: 'Last successful update',
  never: 'never',
  notProtectiveTitle: 'Not a protective device.',
  notProtectiveBody:
    'Switchboard is best-effort load management based on Home Assistant data. It does not replace circuit breakers or EVSE protection.',
};

export type Messages = { [K in keyof typeof en]: string };
