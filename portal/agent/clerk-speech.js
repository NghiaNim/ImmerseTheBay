// Short original clerk lines, separate from the customer's practice overlay.
const lines = {
  welcome: {
    text: 'いらっしゃいませ。お探しのものがございましたら、お声がけください。',
    romaji: 'Irasshaimase. Osagashi no mono ga gozaimashitara, okoegake kudasai.',
    english: 'Welcome. Please let me know if you’re looking for anything.',
  },
  home: {
    text: 'こんにちは。どうぞ、ゆっくりお過ごしください。',
    romaji: 'Konnichiwa. Dōzo, yukkuri osugoshi kudasai.',
    english: 'Hello. Please make yourself comfortable.',
  },
  rejoin: {
    text: 'それでは、先ほどのお話に戻りましょう。',
    romaji: 'Sore de wa, sakihodo no ohanashi ni modorimashō.',
    english: 'Now, let’s get back to what we were discussing.',
  },
  label: {
    text: 'こちらの商品ですね。表示を一緒に確認しましょう。',
    romaji: 'Kochira no shōhin desu ne. Hyōji o issho ni kakunin shimashō.',
    english: 'This is the item, right? Let’s check the label together.',
  },
  checkout: {
    text: 'お会計はこちらです。ご用意ができましたら、お声がけください。',
    romaji: 'Okaikei wa kochira desu. Goyōi ga dekimashitara, okoegake kudasai.',
    english: 'You can pay here. Let me know when you’re ready.',
  },
  shelf: {
    text: 'こちらの棚をご覧ください。ご不明な点があれば、お尋ねください。',
    romaji: 'Kochira no tana o goran kudasai. Gofumei na ten ga areba, otazune kudasai.',
    english: 'Please take a look at this shelf. Let me know if you have any questions.',
  },
  heat: {
    text: '温めをご希望ですね。少々お待ちください。',
    romaji: 'Atatame o gokibō desu ne. Shōshō omachi kudasai.',
    english: 'You’d like it heated up, right? One moment, please.',
  },
  idleMain: {
    text: '何かお探しですか。気になる商品があれば、お聞かせください。',
    romaji: 'Nanika osagashi desu ka? Ki ni naru shōhin ga areba, okikase kudasai.',
    english: 'Are you looking for anything? Let me know if there’s an item you’re interested in.',
  },
  idleDetour: {
    text: '別の商品をご覧になりますか。それとも、元のお話に戻りましょうか。',
    romaji: 'Betsu no shōhin o goran ni narimasu ka? Soretomo, moto no ohanashi ni modorimashō ka?',
    english: 'Would you like to see another item, or shall we return to what we were discussing?',
  },
  cafeIdle: {
    text: 'お決まりですか。',
    romaji: 'Okimari desu ka?',
    english: 'Have you decided?',
  },
  cafeIdleDetour: {
    text: 'ほかに何かありますか。',
    romaji: 'Hoka ni nanika arimasu ka?',
    english: 'Anything else?',
  },
  retry: {
    text: 'すみません。もう一度お願いします。',
    romaji: 'Sumimasen. Mō ichido onegaishimasu.',
    english: 'Sorry. Could you say that again?',
  },
  received: {
    text: 'はい、わかりました。',
    romaji: 'Hai, wakarimashita.',
    english: 'Yes, I understand.',
  },
  farewell: {
    text: 'ありがとうございました。',
    romaji: 'Arigatō gozaimashita.',
    english: 'Thank you.',
  },
  acknowledge: {
    text: '承知しました。ご不明な点がございましたら、お声がけください。',
    romaji: 'Shōchi shimashita. Gofumei na ten ga gozaimashitara, okoegake kudasai.',
    english: 'Understood. Please let me know if you have any questions.',
  },
};

export function clerkSpeech({ first, route, cue = '', store = true }) {
  let line;
  if (first) line = store ? lines.welcome : lines.home;
  else if (route?.kind === 'rejoin') line = lines.rejoin;
  else if (/wrapper|package|label/iu.test(cue)) line = lines.label;
  else if (/basket|checkout|counter/iu.test(cue)) line = lines.checkout;
  else if (/shelf|aisle/iu.test(cue)) line = lines.shelf;
  else if (/heat|warm|microwave/iu.test(cue)) line = lines.heat;
  else line = lines.acknowledge;
  return { role: 'clerk', language: 'ja', ...line };
}

export function clerkInvitation(isDetour, cafe = false) {
  const line = cafe ? (isDetour ? lines.cafeIdleDetour : lines.cafeIdle) : (isDetour ? lines.idleDetour : lines.idleMain);
  return { role: 'clerk', language: 'ja', ...line };
}

export function clerkMenuSelection(choice) {
  const line = choice === 'milk'
    ? { text: 'ミルク入りですね？', romaji: 'Miruku iri desu ne?', english: 'With milk, right?' }
    : { text: 'コーヒーですね？', romaji: 'Kōhī desu ne?', english: 'Coffee, right?' };
  return { role: 'clerk', language: 'ja', ...line };
}

export function clerkReaction(attempt, practicedLine, complete) {
  if (!attempt || attempt.outcome === 'skip') return null;
  const authored = practicedLine?.reaction;
  const line = attempt.outcome === 'retry' ? lines.retry
    : authored ? { text: authored.jp, romaji: authored.romaji, english: authored.en }
    : complete ? lines.farewell : lines.received;
  return { role: 'clerk', language: 'ja', ...line };
}
