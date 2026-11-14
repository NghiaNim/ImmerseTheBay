// Short original clerk lines, separate from the customer's practice overlay.
// Avoid invented prices, ingredients, availability and completed transactions.
export function clerkSpeech({ first, route, cue = '', store = true }) {
  let text;
  if (first) text = store ? 'いらっしゃいませ。お探しのものがございましたら、お声がけください。' : 'こんにちは。どうぞ、ゆっくりお過ごしください。';
  else if (route?.kind === 'rejoin') text = 'それでは、先ほどのお話に戻りましょう。';
  else if (/wrapper|package|label/iu.test(cue)) text = 'こちらの商品ですね。表示を一緒に確認しましょう。';
  else if (/basket|checkout|counter/iu.test(cue)) text = 'お会計はこちらです。ご用意ができましたら、お声がけください。';
  else if (/shelf|aisle/iu.test(cue)) text = 'こちらの棚をご覧ください。ご不明な点があれば、お尋ねください。';
  else if (/heat|warm|microwave/iu.test(cue)) text = '温めをご希望ですね。少々お待ちください。';
  else text = '承知しました。ご不明な点がございましたら、お声がけください。';
  return { role: 'clerk', text, language: 'ja' };
}
