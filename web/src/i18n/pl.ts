import type { Messages } from './en';

// Vocabulary follows docs/mockups ("Obserwacja", "Sterowanie aktywne", the protective-device note).
export const pl: Messages = {
  loading: 'Wczytywanie stanu…',
  rowMode: 'Tryb pracy',
  rowHomeAssistant: 'Home Assistant',
  rowMqtt: 'MQTT',
  rowHealth: 'Kondycja',
  rowVersion: 'Wersja',
  modeShadow: 'Obserwacja',
  modeShadowNote: 'Liczy i zapisuje decyzje, niczego nie przełącza.',
  modeActive: 'Sterowanie aktywne',
  modeActiveNote: 'Ta wersja nie ma ścieżki sterowania i niczego nie przełączy.',
  connectionConnected: 'Połączono',
  connectionConnecting: 'Łączenie',
  connectionDisconnected: 'Rozłączono',
  healthHealthy: 'Sprawny',
  healthUnhealthy: 'Niesprawny',
  lastUpdated: 'Ostatnia aktualizacja',
  unavailable: 'Stan niedostępny',
  lastSuccessfulUpdate: 'Ostatnia udana aktualizacja',
  never: 'nigdy',
  notProtectiveTitle: 'To nie jest urządzenie zabezpieczające.',
  notProtectiveBody:
    'Switchboard ogranicza pobór mocy najlepiej, jak potrafi, na podstawie danych z Home Assistant. Nie zastępuje wyłączników nadprądowych ani zabezpieczeń ładowarki (EVSE).',
};
