# SceneScript 公开接口导航

来源：官方 reference/class、reference/event 和 reference/module 页面。

这是从文档提取的导航清单，不是经过运行验证的完整 SDK 或严格类型定义。

[官方类型定义](https://docs.wallpaperengine.io/reference/lib.sceneScript.d.ts)

版本差异与实现注意事项见 [研究报告](../../docs/WALLPAPER-ENGINE-RESEARCH.md)。

## AnimationEvent

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/class/AnimationEvent.html)

```text
name: String
frame: Number
```

## AudioBuffers

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/class/AudioBuffers.html)

```text
average: Float32Array
left: Float32Array
right: Float32Array
```

## CameraTransforms

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/class/CameraTransforms.html)

```text
eye: Vec3
center: Vec3
up: Vec3
zoom: Number
```

## CursorEvent

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/class/CursorEvent.html)

```text
worldPosition: Vec3
localPosition: Vec3
hitBox: String?
```

## IAnimation

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/class/IAnimation.html)

```text
fps: Number (readonly)
frameCount: Number (readonly)
duration: Number (readonly)
name: String (readonly)
rate: Number
play(): void
stop(): void
pause(): void
isPlaying(): Boolean
getFrame(): Number
setFrame(frame: Number): void
```

## IAnimationLayer

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/class/IAnimationLayer.html)

```text
fps: Number (readonly)
frameCount: Number (readonly)
duration: Number (readonly)
name: String (readonly)
rate: Number
blend: Number
visible: Boolean
play(): void
stop(): void
pause(): void
isPlaying(): Boolean
getFrame(): Number
setFrame(frame: Number)
addEndedCallback(callback: Function)
```

## IAssetHandle

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/class/IAssetHandle.html)

SceneScript Class IAssetHandle Internal object. You get this from engine.registerAsset() and can use it as a parameter for thisScene .createLayer() .

## IConsole

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/class/IConsole.html)

```text
log(...args): void
error(...args): void
```

## IEffect

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/class/IEffect.html)

```text
visible: Boolean
name: String
getMaterial(index: Number): IMaterial
getMaterialCount(): Number
setMaterialProperty(propertyName: String, value: Number|Vec2|Vec3|Vec4)
executeMaterialFunction(propertyName: String): void
```

## IEffectLayer

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/class/IEffectLayer.html)

```text
size: Vec2
perspective: Boolean
solid: Boolean
getEffect(name: String|Number): IEffect
getEffectCount(): Number
transformAttachmentToTexture(attachmentLayer:String|Number|ILayer, attachmentName: String|Number): Mat3
```

## IEngine

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/class/IEngine.html)

```text
screenResolution: Vec2
canvasSize: Vec2
userProperties: Object
timeOfDay: Number
frametime: Number
runtime: Number
AUDIO_RESOLUTION_16: Number
AUDIO_RESOLUTION_32: Number
AUDIO_RESOLUTION_64: Number
isDesktopDevice(): Boolean
isMobileDevice(): Boolean
isWallpaper(): Boolean
isScreensaver(): Boolean
isPortrait(): Boolean
isLandscape(): Boolean
isRunningInEditor(): Boolean
openUserShortcut(userPropertyName: String): Boolean
registerAudioBuffers(resolution: Number): AudioBuffers
registerAsset(file: String): IAssetHandle
setTimeout(callback: Function, delay?: Number): Function
setInterval(callback: Function, delay?: Number): Function
setTimeout();
registerAudioBuffers()
createLayer()
```

## IImageLayer

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/class/IImageLayer.html)

```text
size: Vec2
perspective: Boolean
solid: Boolean
getEffect(name: String|Number): IEffect
getEffectCount(): Number
getTextureAnimation(): ITextureAnimation
getVideoTexture(): IVideoTexture
playSingleAnimation(animation: String|Object, config?: Object): IAnimationLayer
getAnimationLayerCount(): Number
getAnimationLayer(name: String|Number): IAnimationLayer
createAnimationLayer(animation: String|Object): IAnimationLayer
destroyAnimationLayer(animationLayer: String|Object|IAnimationLayer): Boolean
getBoneCount(): Number
getBoneTransform(bone: String|Number): Mat4
setBoneTransform(bone: String|Number, transform: Mat4): void
getLocalBoneTransform(bone: String|Number): Mat4
setLocalBoneTransform(bone: String|Number, transform: Mat4): void
getLocalBoneAngles(bone: String|Number): Vec3
setLocalBoneAngles(bone: String|Number, angles: Vec3): void
getLocalBoneOrigin(bone: String|Number): Vec3
setLocalBoneOrigin(bone: String|Number, origin: Vec3): void
getBoneIndex(name: String): Number
getBoneParentIndex(child: Number|String): Number
applyBonePhysicsImpulse(bone?: String|Number, directionalImpulse: Vec3, angularImpulse: Vec3): void
resetBonePhysicsSimulation(bone?: String|Number): void
getBlendShapeIndex(name: String): Number
getBlendShapeWeight(blendShape: String|Number): Number
setBlendShapeWeight(blendShape: String|Number, weight: Number): void
```

## IInput

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/class/IInput.html)

```text
cursorWorldPosition: Vec3
cursorScreenPosition: Vec2
cursorLeftDown: Boolean
```

## ILayer

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/class/ILayer.html)

```text
origin: Vec3
angles: Vec3
scale: Vec3
name: String
visible: Boolean
parallaxDepth: Vec2
getAnimation(name: String): IAnimation
rotateObjectSpace(angles: Vec3)
setParent(parent: String|Number|ILayer, adjustTransforms?: Boolean)
setParent(parent: String|Number|ILayer, attachment: String|Number, adjustTransform?: Boolean)
getParent(): ILayer
getChildren(): [ILayer]
getAttachmentIndex(name: String): Number
getAttachmentMatrix(attachment: String|Number): Mat4
getAttachmentOrigin(attachment: String|Number): Vec3
getAttachmentAngles(attachment: String|Number): Vec3
```

## ILocalStorage

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/class/ILocalStorage.html)

```text
get(key: String, location?: String): any
set(key: String, value: any, location?: String): void
clear(location?: String): void
delete(key: String, location?: String): Boolean
```

## IMaterial

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/class/IMaterial.html)

SceneScript Class IMaterial This class holds no static properties you should use, but you can access dynamic properties from materials. To do this, bind a script to any shader property, like Alpha or Color and use the dynamic auto-completion in the SceneScript editor from thisObject to discover them.

## IModelData

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/class/IModelData.html)

```text
applyData(shapes: Object|Array): void
replaceData(shapes: Object|Array): void
update()
applyUserProperties()
```

## IModelLayer

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/class/IModelLayer.html)

```text
perspective: Boolean
rootmotion: Boolean
playSingleAnimation(animation: String|Object, config?: Object): IAnimationLayer
getAnimationLayerCount(): Number
getAnimationLayer(name: String|Number): IAnimationLayer
createAnimationLayer(animation: String|Object): IAnimationLayer
destroyAnimationLayer(animationLayer: String|Object|IAnimationLayer): Boolean
```

## IParticleSystem

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/class/IParticleSystem.html)

```text
instance: IParticleSystemInstance
play(): void
pause(): void
stop(): void
isPlaying(): Boolean
emitParticles(count: Number?): void
```

## IParticleSystemInstance

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/class/IParticleSystemInstance.html)

```text
alpha: Number
size: Number
count: Number
speed: Number
lifetime: Number
rate: Number
colorn: Number
controlpoint0: Vec3
controlpoint1: Vec3
controlpoint2: Vec3
controlpoint3: Vec3
controlpoint4: Vec3
controlpoint5: Vec3
controlpoint6: Vec3
controlpoint7: Vec3
```

## IScene

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/class/IScene.html)

```text
bloom: Boolean
bloomstrength: Number
bloomthreshold: Number
clearenabled: Boolean
clearcolor: Vec3
ambientcolor: Vec3
skylightcolor: Vec3
fov: Number
nearz: Number
farz: Number
camerafade: Boolean
camerashake: Boolean
camerashakespeed: Number
camerashakeamplitude: Number
camerashakeroughness: Number
cameraparallax: Boolean
cameraparallaxamount: Number
cameraparallaxdelay: Number
cameraparallaxmouseinfluence: Number
getLayer(name: String|Number): ILayer
getLayerCount(): Number
enumerateLayers(): ILayer[]
destroyLayer(name: String|Number|ILayer): Boolean
createLayer(configuration: String|Object|IAssetHandle): ILayer
sortLayer(layer: String|Number|ILayer, index: Number): Boolean
getLayerIndex(layer: String|ILayer): Number
getInitialLayerConfig(layer: String|Number|ILayer): Object;
getCameraTransforms(): CameraTransforms
setCameraTransforms(cameraTransforms: CameraTransforms): void
createModelData(configuration: Object): IModelData
destroyModelData(modelDataHandle: IModelData)
```

## ISoundLayer

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/class/ISoundLayer.html)

```text
volume: float
isPlaying(): Boolean
play(): void
stop(): void
pause(): void
```

## ITextLayer

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/class/ITextLayer.html)

```text
text: String
color: Vec3
alpha: Number
opaquebackground: Boolean
backgroundcolor: Vec3
pointsize: Number
font: String
padding: Number
horizontalalign: String
verticalalign: String
anchor: String
limitrows: Boolean
maxrows: Number
limitwidth: Boolean
maxwidth: Number
```

## ITextureAnimation

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/class/ITextureAnimation.html)

```text
frameCount: Number (readonly)
duration: Number (readonly)
rate: Number
play()
stop()
pause()
isPlaying(): Boolean
getFrame(): Number
setFrame(frame: Number)
join()
```

## IThisPropertyObject

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/class/IThisPropertyObject.html)

```text
getAnimation(name?: String): IAnimation
```

## IVideoTexture

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/class/IVideoTexture.html)

```text
duration: Number
rate: Number
loop: Boolean
play(): void
pause(): void
stop(): void
isPlaying(): Boolean
getCurrentTime(): Number
setCurrentTime(time: Number): void
addEndedCallback(callback: Function): void
```

## Mat3

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/class/Mat3.html)

```text
m: Array
constructor(): Mat3
static identity(): Mat3
static fromTranslation(v: Vec2): Mat3
static fromScale(v: Number|Vec2): Mat3
static fromRotation(angle: Number): Mat3
static fromBasis(right: Vec2, up: Vec2): Mat3
static fromMat4(m: Mat4): Mat3
static compose(translation: Vec2, rotation: Number, scale: Vec2): Mat3
translation(position?: Vec2): Vec2
angle(): Number
add(other: Mat3): Mat3
subtract(other: Mat3): Mat3
multiply(value: Mat3|Vec3|Number): Mat3|Vec3
translate(v: Vec2): Mat3
rotate(angle: Number): Mat3
scale(v: Number|Vec2): Mat3
transformPoint(v: Vec2): Vec2
transformDirection(v: Vec2): Vec2
transpose(): Mat3
determinant(): Number
inverse(): Mat3
decompose(): Object
copy(): Mat3
equals(other: Mat3): Boolean
toString(): String
```

## Mat4

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/class/Mat4.html)

```text
constructor(): Mat4
static identity(): Mat4
static fromTranslation(v: Vec2|Vec3): Mat4
static fromScale(v: Number|Vec3): Mat4
static fromRotation(angle: Number, axis: Vec3): Mat4
static fromEuler(x: Number|Vec3, y?: Number, z?: Number): Mat4
static fromBasis(right: Vec3, up: Vec3, forward: Vec3): Mat4
static lookAt(eye: Vec3, center: Vec3, up: Vec3): Mat4
static compose(translation: Vec3, rotation: Vec3, scale: Vec3): Mat4
translation(position?: Vec2|Vec3): Vec3
right(): Vec3
up(): Vec3
forward(): Vec3
add(other: Mat4): Mat4
subtract(other: Mat4): Mat4
multiply(value: Mat4|Vec4|Number): Mat4|Vec4
translate(v: Vec2|Vec3): Mat4
rotate(angle: Number, axis: Vec3): Mat4
scale(v: Number|Vec3): Mat4
transformPoint(v: Vec3): Vec3
transformDirection(v: Vec3): Vec3
transpose(): Mat4
inverse(): Mat4
determinant(): Number
extractEuler(): Vec3
normalMatrix(): Mat3
decompose(): Object
copy(): Mat4
equals(other: Mat4): Boolean
toString(): String
```

## MediaPlaybackEvent

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/class/MediaPlaybackEvent.html)

```text
state: Number
```

## MediaPropertiesEvent

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/class/MediaPropertiesEvent.html)

```text
title: String
artist: String
contentType: String
albumTitle: String
subTitle: String
albumArtist: String
genres: String
```

## MediaStatusEvent

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/class/MediaStatusEvent.html)

```text
enabled: Boolean
```

## MediaThumbnailEvent

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/class/MediaThumbnailEvent.html)

```text
hasThumbnail: Boolean
primaryColor: Vec3
secondaryColor: Vec3
tertiaryColor: Vec3
textColor: Vec3
highContrastColor: Vec3
```

## MediaTimelineEvent

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/class/MediaTimelineEvent.html)

```text
position: Number
duration: Number
```

## Shared

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/class/Shared.html)

SceneScript Class Shared The global object shared is an empty object you may use to share data between multiple scripts. Prefer using this object over declaring your own global objects or adding members to existing ones.

## Vec2

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/class/Vec2.html)

```text
x: Number
y: Number
constructor(x: Number, y: Number): Vec2
constructor(): Vec2
constructor(value: Number): Vec2
constructor(value: String): Vec2
constructor(value: Vec3): Vec2
length(): Number
lengthSqr(): Number
distance(other: Vec2): Number;
distanceSqr(other: Vec2): Number;
normalize(): Vec2
copy(): Vec2
equals(other: Vec2): Boolean;
isFinite(): Boolean;
negate(): Vec2;
add(value: Number|Vec2): Vec2
subtract(value: Number|Vec2): Vec2
multiply(value: Number|Vec2): Vec2
divide(value: Number|Vec2): Vec2
dot(value: Vec2): Number
reflect(normal: Vec2): Vec2
mix(other: Vec2, amount: Number): Vec2
min(value: Vec2): Vec2
max(value: Vec2): Vec2;
clamp(min: Number|Vec2, max: Number|Vec2): Vec2;
perpendicular(): Vec2
abs(): Vec2
sign(): Vec2
round(): Vec2
floor(): Vec2
ceil(): Vec2
fract(): Vec2;
mod(value: Number|Vec2): Vec2;
step(edge: Number|Vec2): Vec2;
smoothStep(min: Number|Vec2, max: Number|Vec2): Vec2;
toString(): String
```

## Vec3

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/class/Vec3.html)

```text
x: Number
y: Number
z: Number
constructor(x: Number, y: Number, z: Number): Vec3
constructor(): Vec3
constructor(value: Number): Vec3
constructor(x: Number, y: Number): Vec3
constructor(value: String): Vec3
constructor(value: Vec2): Vec3
static fromSpherical(r: Number, theta: Number, phi: Number): Vec3;
equals(other: Vec3): Boolean
distance(other: Vec3): Number;
distanceSqr(other: Vec3): Number;
length(): Number
lengthSqr(): Number
normalize(): Vec3
copy(): Vec3
isFinite(): Boolean;
negate(): Vec3;
add(value: Number|Vec2|Vec3): Vec3
subtract(value: Number|Vec2|Vec3): Vec3
multiply(value: Number|Vec2|Vec3): Vec3
divide(value: Number|Vec2|Vec3): Vec3
dot(value: Vec3): Number
reflect(normal: Vec3): Vec3
refract(normal: Vec3, eta: Number): Vec3;
project(value: Vec3): Vec3;
angleBetween(value: Vec3): Number;
toSpherical(): Vec3;
mix(other: Vec3, amount: Number): Vec3
min(value: Vec3): Vec3
max(value: Vec3): Vec3;
cross(value: Vec3): Vec3
clamp(min: Number|Vec3, max: Number|Vec3): Vec3;
abs(): Vec3
sign(): Vec3
round(): Vec3
floor(): Vec3
ceil(): Vec3
fract(): Vec3;
mod(value: Number|Vec3): Vec3;
step(edge: Number|Vec3): Vec3;
smoothStep(min: Number|Vec3, max: Number|Vec3): Vec3;
toString(): String
```

## Vec4

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/class/Vec4.html)

```text
x: Number
y: Number
z: Number
w: Number
constructor(x: Number|Vec2|Vec3|Vec4|String, y?: Number, z?: Number, w?: Number): Vec4
length(): Number
lengthSqr(): Number
distance(other: Vec4): Number
distanceSqr(other: Vec4): Number
normalize(): Vec4
copy(): Vec4
equals(other: Vec4): Boolean
isFinite(): Boolean
negate(): Vec4
add(value: Number|Vec4): Vec4
subtract(value: Number|Vec4): Vec4
multiply(value: Number|Vec4): Vec4
divide(value: Number|Vec4): Vec4
dot(value: Vec4): Number
reflect(normal: Vec4): Vec4
project(value: Vec4): Vec4
mix(other: Vec4, amount: Number|Vec4): Vec4
min(value: Vec4): Vec4
max(value: Vec4): Vec4
clamp(min: Number|Vec4, max: Number|Vec4): Vec4
abs(): Vec4
sign(): Vec4
round(): Vec4
floor(): Vec4
ceil(): Vec4
fract(): Vec4
mod(value: Number|Vec4): Vec4
step(edge: Number|Vec4): Vec4
smoothStep(min: Number|Vec4, max: Number|Vec4): Vec4
toString(): String
```

## applyGeneralSettings

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/event/applyGeneralSettings.html)

```text
hasOwnProperty()
```

## applyUserProperties

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/event/applyUserProperties.html)

```text
hasOwnProperty()
update()
```

## cursor

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/event/cursor.html)

SceneScript Cursor Events SceneScript offers multiple cursor event that allows you to interact with mouse movements and mouse clicks. The parameter for all cursor events is an CursorEvent object with additional event data. All mouse cursor events will only work on objects marked as Solid in the layer settings. cursorEnter cursorLeave cursorMove cursorDown cursorUp cursorClick cursorEnter This event function will be called when the cursor enters the bounds of the object. export function cursorEnter ( event : CursorEvent ) { } cursorLeave This event function will be called when the cursor leaves the bounds of the object. export function cursorLeave ( event : CursorEvent ) { } cursorMove This e

## destroy

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/event/destroy.html)

SceneScript Event destroy This event function will be called just before the object it is attached to gets destroyed. export function destroy ( ) { }

## init

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/event/init.html)

```text
update()
init()
Vec3(2, 2, 2)
```

## media

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/event/media.html)

SceneScript Media Integration Events SceneScript offers multiple media integration events that allow you to access information about currently playing music or videos on the system. This will only be available for media players that integrate with the global Windows media system. Some media players require this feature to be turned on first. Please note Some media data is not available in certain audio players. Always build your wallpapers in a way that they do not break when the feature is disabled or if certain data points are unavailable. mediaPlaybackChanged Hide media visualizer while no audio is being played mediaStatusChanged mediaPropertiesChanged mediaThumbnailChanged mediaTimelineC

## resizeScreen

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/event/resizeScreen.html)

SceneScript Event resizeScreen This function will be called every time the wallpaper resizes because of a change to the current screen resolution. It is useful if you want to adjust wallpaper elements when the user changes their screen resolution or orientation. The parameter size is a Vec2 object that contains the new resolution, where size.x is the new width and size.y is the new height. export function resizeScreen ( size : Vec2 ) { } Usually, it makes sense to combine it with the init event if your resizing logic is entirely happening in SceneScript. Sample Usage In the example below, we want a layer to change its scale depending on the screen resolution. An example use-case for this wou

## update

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/event/update.html)

```text
Vec3(2, 2, 2)
update()
```

## WEColor

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/module/WEColor.html)

```text
rgb2hsv(rgb: Vec3): Vec3
hsv2rgb(hsv: Vec3): Vec3
normalizeColor(rgb: Vec3): Vec3
expandColor(rgb: Vec3): Vec3
```

## WEMath

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/module/WEMath.html)

```text
deg2rad: Number
rad2deg: Number
smoothStep(min: Number, max: Number, value: Number): Number
mix(a: Number, b: Number, value: Number): Number
```

## WEVector

[官方页面](https://docs.wallpaperengine.io/en/scene/scenescript/reference/module/WEVector.html)

```text
angleVector2(angle: Number): Vec2
vectorAngle2(direction: Vec2): Number
```
