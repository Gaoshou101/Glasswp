const {chromium} = require('C:/Users/Xu/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const fs = require('fs');
const path = require('path');
(async () => {
  const browser = await chromium.launch({headless:true});
  const page = await browser.newPage({viewport:{width:1440,height:1100}});
  const observations = [];
  for (const id of process.argv.slice(2)) {
    try {
      await page.goto('https://steamcommunity.com/sharedfiles/filedetails/?id='+id, {waitUntil:'domcontentloaded',timeout:45000});
      await page.locator('.workshopItemTitle').waitFor({timeout:10000});
      const htmlPath = path.join(__dirname,'raw',`item-${id}.html`);
      if (!fs.existsSync(htmlPath)) fs.writeFileSync(htmlPath,await page.content());
      await page.waitForFunction(() => [...document.querySelectorAll('#highlight_player_area img')].some(e => e.naturalWidth > 100), {timeout:15000}).catch(()=>{});
      await page.waitForTimeout(1500);
      await page.screenshot({path:path.join(__dirname,'raw',`preview-${id}.png`)});
      const state = await page.evaluate(() => ({title:document.title, images:[...document.querySelectorAll('#highlight_player_area img')].map(e=>({src:e.src,width:e.naturalWidth})), frames:[...document.querySelectorAll('iframe')].map(e=>e.src), videos:[...document.querySelectorAll('video')].map(e=>({src:e.src,paused:e.paused}))}));
      observations.push({id,...state});
      console.log(JSON.stringify({id,...state}));
    } catch(e) { observations.push({id,error:e.message}); console.log(id,e.message); }
  }
  const observationsPath = path.join(__dirname,'preview-observations.json');
  const prior = fs.existsSync(observationsPath) ? JSON.parse(fs.readFileSync(observationsPath,'utf8')).filter(r=>!observations.some(o=>o.id===r.id)) : [];
  fs.writeFileSync(observationsPath,JSON.stringify([...prior,...observations],null,2));
  await browser.close();
})();
