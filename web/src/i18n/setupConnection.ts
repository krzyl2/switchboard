// Setup wizard, step 2 (Connection): its own texts, so the steps can be built in parallel
// without editing one file. Keys start with 'setupConnection.'; both languages here (mockup vocabulary).

const en = {
  'setupConnection.title': 'Connection parameters',
  'setupConnection.stepLine': 'Step 2 of 5',
  'setupConnection.lead': 'The main breaker and the reserve kept for the loads the system does not control.',

  'setupConnection.layout.title': 'Installation layout and breaker rating',
  'setupConnection.layout.rating': '{rating} A per phase',
  'setupConnection.layout.missing': 'not chosen yet',
  'setupConnection.layout.change': 'Change in step 1',
  'setupConnection.layout.hint': 'Asked in step 1, because they decide which meter entities are needed.',

  'setupConnection.characteristic.label': 'Characteristic',
  'setupConnection.characteristic.choose': 'choose',
  'setupConnection.characteristic.B': 'B (residential)',
  'setupConnection.characteristic.C': 'C',
  'setupConnection.characteristic.unknown': 'I don’t know — cautious mode',
  'setupConnection.characteristic.hint': 'The letter printed on the breaker before its rating (B25). Cautious mode takes the rating itself as the continuous limit.',
  'setupConnection.voltage.label': 'Phase voltage',
  'setupConnection.voltage.hint': 'volts',

  'setupConnection.reserve.title': 'Power reserve per phase',
  'setupConnection.reserve.hint':
    'How much power to keep free for the loads the system does not control — a kettle, an oven, an induction hob. It does not have to be the largest of them: a load has to fit under the breaker’s curve, not under its rating (the arithmetic below).',
  'setupConnection.reserve.phase': 'Reserve on {phase} (W)',
  'setupConnection.reserve.minimum': 'Minimum safe reserve {minimum} W, from a refresh every {refresh} (detected).',
  'setupConnection.reserve.minimumMaxAge': 'Minimum safe reserve {minimum} W, from the max age of {refresh} (refresh not detected yet: the worst case).',
  'setupConnection.reserve.below': 'below the minimum safe reserve — shown, never enforced',
  'setupConnection.reserve.empty': 'Enter the reserve: the minimum safe reserve and the arithmetic below are judged against it.',
  'setupConnection.reserve.noMinimum': 'The minimum safe reserve appears once this phase’s current and its max age are mapped in step 1.',
  'setupConnection.reserve.why': 'Why a minimum: between two meter reports the system sees nothing, so the slower the meter, the more must stay free.',

  'setupConnection.math.title': 'The kettle arithmetic, with your numbers',
  'setupConnection.math.head': '{rating} on a phase is {phase}.',
  'setupConnection.math.allocatable': 'With a reserve of {reserve}, the system distributes at most {allocatable} per phase among its own loads.',
  'setupConnection.math.kettle': 'A kettle ({step}) switched on at the worst moment, on a phase already filled up to the reserve, brings the current to about {worst}, i.e. {multiple} × the breaker rating.',
  'setupConnection.math.under': 'That stays under the breaker rating: nothing needs to be done.',
  'setupConnection.math.reduce': 'The breaker curve tolerates that for up to {allowed}; within that time the system plans a reduction (while control is not in force it only records it).',
  'setupConnection.math.shed': 'The reserve is too small: a kettle would force a shed (while control is not in force the system only records it).',
  'setupConnection.math.curve': 'So the reserve may be smaller than the largest appliance: a load has to fit under the breaker’s curve, not under its rating.',
  'setupConnection.math.smallest': 'Computed for the smallest of the phase reserves.',
  'setupConnection.math.pending': 'The arithmetic appears once the layout, breaker rating, characteristic, voltage and every reserve are entered.',
  'setupConnection.math.recalculating': 'Recalculating with your change…',
  'setupConnection.math.failed': 'The add-on did not answer, so the numbers cannot be shown. Try again.',

  'setupConnection.check.run': 'Check and continue',
  'setupConnection.check.running': 'Checking…',
  'setupConnection.check.refused': 'Not continued: fix “{field}” first.',
  'setupConnection.check.failed': 'The check could not be run: the add-on did not answer. Try again.',
};

export const setupConnection: { en: typeof en; pl: Record<keyof typeof en, string> } = {
  en,
  pl: {
    'setupConnection.title': 'Parametry przyłącza',
    'setupConnection.stepLine': 'Krok 2 z 5',
    'setupConnection.lead': 'Bezpiecznik główny i zapas mocy zostawiany na odbiorniki, których system nie kontroluje.',

    'setupConnection.layout.title': 'Układ instalacji i prąd bezpiecznika',
    'setupConnection.layout.rating': '{rating} A na fazę',
    'setupConnection.layout.missing': 'jeszcze nie wybrano',
    'setupConnection.layout.change': 'Zmień w kroku 1',
    'setupConnection.layout.hint': 'Pytamy o to w kroku 1, bo od tego zależy, jakie encje licznika są potrzebne.',

    'setupConnection.characteristic.label': 'Charakterystyka',
    'setupConnection.characteristic.choose': 'wybierz',
    'setupConnection.characteristic.B': 'B (mieszkaniowa)',
    'setupConnection.characteristic.C': 'C',
    'setupConnection.characteristic.unknown': 'Nie wiem — tryb ostrożny',
    'setupConnection.characteristic.hint': 'Litera nadrukowana na bezpieczniku przed prądem (B25). Tryb ostrożny przyjmuje sam prąd znamionowy jako limit ciągły.',
    'setupConnection.voltage.label': 'Napięcie fazowe',
    'setupConnection.voltage.hint': 'woltów',

    'setupConnection.reserve.title': 'Zapas mocy na fazę',
    'setupConnection.reserve.hint':
      'Ile mocy zostawić wolnej na odbiorniki, których system nie kontroluje — czajnik, piekarnik, kuchenkę indukcyjną. Nie musi to być moc największego z nich: odbiornik musi zmieścić się pod krzywą bezpiecznika, a nie pod jego limitem (wyliczenie poniżej).',
    'setupConnection.reserve.phase': 'Zapas na {phase} (W)',
    'setupConnection.reserve.minimum': 'Minimalny bezpieczny zapas {minimum} W, z odświeżania co {refresh} (wykryte).',
    'setupConnection.reserve.minimumMaxAge': 'Minimalny bezpieczny zapas {minimum} W, z maksymalnego wieku {refresh} (odświeżanie jeszcze niewykryte: najgorszy przypadek).',
    'setupConnection.reserve.below': 'poniżej minimalnego bezpiecznego zapasu — tylko informacja, system go nie wymusza',
    'setupConnection.reserve.empty': 'Wpisz zapas: minimalny bezpieczny zapas i wyliczenie poniżej odnoszą się do niego.',
    'setupConnection.reserve.noMinimum': 'Minimalny bezpieczny zapas pojawi się, gdy w kroku 1 przypiszesz prąd tej fazy i jego maksymalny wiek.',
    'setupConnection.reserve.why': 'Skąd minimum: między dwoma odczytami licznika system nic nie widzi, więc im wolniejszy licznik, tym więcej musi zostać wolne.',

    'setupConnection.math.title': 'Wyliczenie dla czajnika, z Twoimi liczbami',
    'setupConnection.math.head': '{rating} na fazie to {phase}.',
    'setupConnection.math.allocatable': 'Z zapasem {reserve} system rozdzieli między swoje odbiorniki maksymalnie {allocatable} na fazę.',
    'setupConnection.math.kettle': 'Czajnik ({step}) włączony w najgorszym momencie, gdy faza jest już zapełniona do zapasu, da prąd około {worst}, czyli {multiple} × prąd bezpiecznika.',
    'setupConnection.math.under': 'To wciąż poniżej prądu bezpiecznika: nic nie trzeba robić.',
    'setupConnection.math.reduce': 'Krzywa bezpiecznika toleruje to do {allowed}; w tym czasie system zaplanuje zmniejszenie obciążenia (dopóki sterowanie nie jest włączone, tylko je zapisuje).',
    'setupConnection.math.shed': 'Zapas jest za mały: czajnik wymusiłby zrzut obciążenia (dopóki sterowanie nie jest włączone, system tylko to zapisuje).',
    'setupConnection.math.curve': 'Dlatego zapas może być mniejszy niż największe urządzenie: odbiornik musi zmieścić się pod krzywą bezpiecznika, a nie pod jego limitem.',
    'setupConnection.math.smallest': 'Wyliczone dla najmniejszego z zapasów faz.',
    'setupConnection.math.pending': 'Wyliczenie pojawi się po wpisaniu układu, prądu bezpiecznika, charakterystyki, napięcia i zapasu każdej fazy.',
    'setupConnection.math.recalculating': 'Przeliczanie po zmianie…',
    'setupConnection.math.failed': 'Dodatek nie odpowiedział, więc liczb nie da się pokazać. Spróbuj ponownie.',

    'setupConnection.check.run': 'Sprawdź i przejdź dalej',
    'setupConnection.check.running': 'Sprawdzanie…',
    'setupConnection.check.refused': 'Nie przechodzę dalej: najpierw popraw „{field}”.',
    'setupConnection.check.failed': 'Nie udało się sprawdzić: dodatek nie odpowiedział. Spróbuj ponownie.',
  },
};
