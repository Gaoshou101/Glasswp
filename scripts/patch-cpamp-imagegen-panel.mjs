// Surgical patch for the deployed CommandCodeBridge panel. Refuse other builds.
import fs from 'node:fs';
import crypto from 'node:crypto';
import vm from 'node:vm';
import assert from 'node:assert/strict';

const [input, output] = process.argv.slice(2);
if (!input || !output || input === output) throw new Error('Supply distinct input/output files');
const original = fs.readFileSync(input, 'utf8');
const hash = (s) => crypto.createHash('sha256').update(s).digest('hex');
if (hash(original) !== '2f8adf720ff9d13b26b44c041ab1e2743a99c9011e7950628d42600fda916a45') throw new Error('Unexpected panel build');
const before = 'fv({model:e?.model,analyticsModel:e?.analytics_model,requestedModel:e?.requested_model,resolvedModel:e?.resolved_model})';
const after = `((s,q)=>{const base=fv(s),limit=String(q.active_limit??'').trim().toLowerCase(),key=limit.replace(/[^a-z0-9]+/g,'_').replace(/^_+|_+$/g,''),model=[s.resolvedModel,s.analyticsModel,s.requestedModel,s.model].map(v=>String(v??'').trim().toLowerCase()).find(Boolean)||'',image=key==='imagegen'||key.startsWith('imagegen_')?key:!limit&&/^(gpt-image-|dall-e-)/.test(model)?'imagegen':'';return image?{modelScope:{kind:'feature',key:image,complete:false},providerWindowIdPrefix:image.replace(/_/g,'-'),labelName:image==='imagegen_premium'?'Image generation (Premium)':image==='imagegen'?'Image generation':image}:base})({model:e?.model,analyticsModel:e?.analytics_model,requestedModel:e?.requested_model,resolvedModel:e?.resolved_model},t)`;
assert.equal(original.split(before).length, 2, 'Adapter call must be unique');
const patched = original.replace(before, after);
assert.equal(patched.replace(after, before), original, 'Only quota scope expression may change');

// Evaluate the exact edited expression with the panel's model resolver contract.
const resolver = (s) => {
  const model = [s.resolvedModel,s.analyticsModel,s.requestedModel,s.model].find(v => v?.trim());
  return model === 'gpt-5.3-codex-spark'
    ? {modelScope:{kind:'models',models:[model],complete:true},providerWindowIdPrefix:'spark'}
    : {modelScope:{kind:'family',key:'codex_main',complete:true},providerWindowIdPrefix:''};
};
const evaluate = (e,t) => vm.runInNewContext(after, {e,t,fv:resolver});
for (const [active,model,resolved,key] of [
  ['imagegen_premium','gpt-image-2.5-sunburst','','imagegen_premium'],
  [' IMAGEGEN_PREMIUM ','my-image','gpt-image-2.5-sunburst','imagegen_premium'],
  ['imagegen_premium','gpt-5.6-sol','','imagegen_premium'],
  ['','my-image','gpt-image-2.5-sunburst','imagegen'],
  ['','gpt-image-2.5-sunburst','gpt-5.6-sol','codex_main'],
  ['premium','gpt-5.6-sol','','codex_main'],
]) {
  const value = evaluate({model,resolved_model:resolved}, {active_limit:active});
  assert.equal(value.modelScope.key,key);
}
assert.equal(evaluate({model:'gpt-5.3-codex-spark'},{}).providerWindowIdPrefix,'spark');
fs.writeFileSync(output, patched);
console.log(JSON.stringify({passed:true,cases:7,onlyScopeExpressionChanged:true,beforeSHA256:hash(original),afterSHA256:hash(patched)}));
