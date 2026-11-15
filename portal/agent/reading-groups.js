// Reviewed Japanese word boundaries and readings for the authored demo turns.
const demoReadings = new Map([
  ['サンドイッチはどこですか。', {
    kana: 'さんどいっちはどこですか。',
    groups: [
      { jp: 'サンドイッチ', romaji: 'Sandoitchi' },
      { jp: 'は', romaji: 'wa' },
      { jp: 'どこ', romaji: 'doko' },
      { jp: 'です', romaji: 'desu' },
      { jp: 'か。', romaji: 'ka?' },
    ],
  }],
  ['お水をください。', {
    kana: 'おみずをください。',
    groups: [
      { jp: 'お水', romaji: 'Omizu' },
      { jp: 'を', romaji: 'o' },
      { jp: 'ください。', romaji: 'kudasai.' },
    ],
  }],
  ['カードで払えますか。', {
    kana: 'かーどではらえますか。',
    groups: [
      { jp: 'カード', romaji: 'Kādo' },
      { jp: 'で', romaji: 'de' },
      { jp: '払えます', romaji: 'haraemasu' },
      { jp: 'か。', romaji: 'ka?' },
    ],
  }],
]);

const foldKana = (kana) => kana.normalize('NFC').replace(/[ァ-ヶ]/gu, (letter) =>
  String.fromCharCode(letter.charCodeAt(0) - 0x60));
const foldRomaji = (romaji) => romaji.normalize('NFC').trim().toLowerCase().replace(/\s+/gu, ' ');

/** Look up reviewed groups only when they agree with all three authored renderings. */
export function readingGroupsFor({ jp, kana, romaji }) {
  const entry = demoReadings.get(jp);
  if (!entry || typeof kana !== 'string' || typeof romaji !== 'string' || foldKana(kana) !== entry.kana)
    return undefined;
  const { groups } = entry;
  if (groups.map((group) => group.jp).join('') !== jp
      || foldRomaji(groups.map((group) => group.romaji).join(' ')) !== foldRomaji(romaji))
    return undefined;
  return groups.map((group) => ({ ...group }));
}
