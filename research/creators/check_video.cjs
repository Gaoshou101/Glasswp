const {chromium}=require('C:/Users/Xu/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const fs=require('fs');
(async()=>{
 const browser=await chromium.launch({headless:true});
 const page=await browser.newPage({viewport:{width:1280,height:900}});
 let result={url:'https://www.youtube.com/watch?v=JHe0GYENWxQ'};
 try {
  await page.goto(result.url,{waitUntil:'domcontentloaded',timeout:25000});
  result.title=await page.title();
  result.text=(await page.locator('body').innerText()).slice(0,3500);
  result.video=await page.evaluate(()=>[...document.querySelectorAll('video')].map(v=>({paused:v.paused,time:v.currentTime,ready:v.readyState})));
  await page.screenshot({path:__dirname+'/raw/video-check.png'});
  await page.evaluate(async()=>{const v=document.querySelector('video');if(v){v.muted=true;await v.play();}});
  result.samples=[];
  for(const seconds of [8,24,40]){
   await page.evaluate(s=>{document.querySelector('video').currentTime=s;},seconds);
   await page.waitForTimeout(2500);
   result.samples.push(await page.evaluate(()=>{const v=document.querySelector('video');return {time:v.currentTime,paused:v.paused,ready:v.readyState};}));
   await page.screenshot({path:__dirname+`/raw/dock2-video-${seconds}.png`});
  }
 } catch(e){result.error=e.message;}
 fs.writeFileSync(__dirname+'/video-check.json',JSON.stringify(result,null,2));
 console.log(JSON.stringify(result));
 await browser.close();
})();
