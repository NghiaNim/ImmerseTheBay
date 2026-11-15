import { SCENE_ANCHOR } from './continuity.js';
import { primaryIntent } from './flow.js';
export const PRESET_SET = 'konbini-original';
// Authored from the approved original image, never inferred from learner text.
export const CHECKOUT_IDENTITY = 'The same young adult woman: oval face, dark brown eyes, dark brown low ponytail with wispy bangs, white collared shirt, beige apron; her gender, age, face, hair and clothing stay unchanged.';
const scene = (id, label, startImage, prompt, visiblePartner) => ({ id, label, startImage, prompt, visiblePartner });
export const SCENES = Object.freeze({
 checkout: scene('checkout', 'Checkout counter', '/lessons/images/konbini-checkout-original.png', `${CHECKOUT_IDENTITY} The clerk waits behind the convenience-store checkout counter in the supplied starting image.`, true),
 sandwiches: scene('sandwiches', 'Sandwich shelf', '/scene-presets/konbini-sandwiches.png', 'First-person eye-level view facing the convenience-store sandwich shelf in the supplied image. Plain sandwich packages fill the existing shelf. The environment and objects retain their original arrangement and lighting.', false),
 drinks: scene('drinks', 'Drinks shelf', '/scene-presets/konbini-drinks.png', 'First-person eye-level view facing the convenience-store drinks shelf in the supplied image. Plain bottles of water and tea fill the existing shelf. The environment and objects retain their original arrangement and lighting.', false),
});
export function classifyPresetIntent(intent) {
 const goal = primaryIntent(typeof intent === 'string' ? intent : '');
 // Match concrete shopping language; avoid substrings such as 水曜日 and idioms.
 if (/water under the bridge|test(?:ing)? the waters|hold water|spill the tea|my cup of tea|茶余饭后|水到渠成|水落石出/iu.test(goal)) return null;
 const checkout = /\b(?:find|locate|where is|where's|looking for|want(?: to go to)?|need to find|take me to|go to)\s+(?:the\s+)?(?:checkout(?:\s+counter)?|counter)\b|(?:找|去|到)(?:一下|那个|那個|这个|這個)?(?:收银台|收銀台|结账台|結帳台)|(?:我想|我要|想要)(?:去)?(?:结账|結帳|结帐)|(?:收银台|收銀台|结账台|結帳台)(?:在哪|在哪里|在哪裡)|(?:レジ|会計)[^。]*(?:どこ|探|行きたい|いきたい)/iu;
 if (checkout.test(goal)) return 'checkout';
 if (/\bsandwich(?:es)?\b|サンドイッチ|さんどいっち|三明治/iu.test(goal)) return 'sandwiches';
 if (/\b(?:water|bottles?|tea)\b|お水|お茶|ボトル|飲料水|矿泉水|礦泉水|(?:买|買|喝|瓶|杯|要|找|的|这个|這個)水|水(?:を|が|は|ください|あります|在哪|在哪里|在哪裡)|^水[。.!！?？\s]*$|饮料|飲料/iu.test(goal)) return 'drinks';
 return null;
}
export function presetDrinkPlan(intent, returnBeat) {
 if (classifyPresetIntent(intent) !== 'drinks') return null;
 const goal = primaryIntent(intent);
 // Complex actions still need their specific bounded planner interaction.
 if (/\b(?:heat|reheat|microwave|refund|exchange|return|allerg(?:y|ies|en)|ingredients?|caffeine|sugar|nutrition)\b|加热|加熱|退货|退貨|退款|换货|換貨|过敏|過敏|配料|原材料|温め|返品|交換|アレルギー|カフェイン|糖/iu.test(goal)) return null;
 return {label:'飲み物を選ぶ · Choose a drink',returnBeat,nodes:[
  {jp:'すみません、お水かお茶はどこにありますか。',kana:'すみません、おみずかおちゃはどこにありますか。',romaji:'Sumimasen, omizu ka ocha wa doko ni arimasu ka.',zh:'请问水或茶在哪里？',en:'Excuse me, where can I find water or tea?',cue:'The plain drink bottles remain arranged on the shelf.'},
  {jp:'こちらを一本お願いします。',kana:'こちらをいっぽんおねがいします。',romaji:'Kochira o ippon onegaishimasu.',zh:'请给我这款一瓶。',en:'I would like one bottle of this, please.',cue:'The selected bottle remains visible on the shelf.'},
 ]};
}
export function updatePreset(state, { entered, intent, route }) {
 if (!state.presetSceneSet) return;
 const id = route.kind === 'rejoin' ? 'checkout' : entered ? classifyPresetIntent(intent) || state.scene?.id || 'checkout' : state.scene?.id || 'checkout';
 if (state.scene && state.scene.id !== id) state.sceneContext.recentActions = [];
 state.scene = structuredClone(SCENES[id]);
 state.sceneContext.sceneAnchor = state.scene.prompt;
 state.sceneContext.visiblePartner = state.scene.visiblePartner;
 state.sceneContext.identityAnchor = state.scene.visiblePartner ? CHECKOUT_IDENTITY : 'The same clerk speaks off-camera. The photographed shelf environment is the only visible subject.';
 state.sceneContext.partnerLocation = state.scene.visiblePartner ? 'at the checkout counter' : 'off-camera; voice only';
 state.sceneContext.referenceId = state.scene.startImage;
}
export function presetEnvironmentPrompt(preset) { return `${preset.prompt} ${SCENE_ANCHOR}`; }
