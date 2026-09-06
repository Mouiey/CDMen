# Emitter Follow + Item Layer Client

面向 Barotrauma 1.13.4.0 / LuaCs 的纯客户端粒子扩展。它给原版
`ParticleEmitter` 增加两项彼此独立的可选能力：父子变换/“心跳停止后淡出”，以及
以人物所在的 `0.5` 深度为边界插入物品式粒子层级。它不修改游戏 DLL，也不创建
任何隐藏物品、射弹或状态效果。

## 安装

1. 将整个 `EmitterFollowClient` 文件夹复制到 Barotrauma 的 `LocalMods`。
2. 在使用效果的客户端启用 `Emitter Follow Client`。
3. 服务器不需要加载 C# 代码；`CSharp/RunConfig.xml` 明确设置为
   `Server=None`、`Client=Standard`。

本目录没有注册物品或粒子 prefab。实际魔法书和 `magic_circle` 粒子仍由拥有
它们的内容包提供。

## XML 接口

```xml
<StatusEffect type="OnSecondaryUse" target="This">
  <ParticleEmitter
      particle="magic_circle"
      itemlayerdepth="true"
      followemitter="true"
      followemitterfadeout="0.2"
      copyentityangle="true"
      particleamount="1"
      particlespersecond="0"
      emitinterval="999999" />
</StatusEffect>
```

- `followemitter` 默认为 `false`。只有设为 `true` 的发射器进入扩展路径。
- `followemitterfadeout` 默认为 `0.2` 秒。`0` 立即删除；负数、NaN、Infinity
  或无法解析的值回退为 `0.2`，整次加载只记录一次警告。
- `itemlayerdepth` 默认为 `false`，可以单独使用，也可以和 `followemitter` 组合。
  启用后读取每个粒子实际随机选中的 Sprite/AnimatedSprite 的 `Depth`：
  `Depth > 0.5` 绘制在后层实体之后、人物之前；`Depth <= 0.5` 绘制在人物之后、
  前层实体之前。同一侧不再与每个物品逐个按深度排序。
- `itemlayerdepth` 同时保留 `AlphaBlend` 与 `Additive` 混合，但要求粒子 prefab 使用
  `DrawTarget="Both"`。`Air`/`Water` 会各 prefab 警告一次并退回原版绘制。
- `itemlayerdepth` 优先于发射器或粒子 prefab 的 `DrawOrder`。此类内容应显式使用
  `DrawOrder="Default"`；其他值会警告一次，但仍按 `itemlayerdepth` 绘制。
- `copyentityangle="true"` 让原版把物品方向传给发射器，因此旋转、贴图角度
  和速度方向才能随物品变化；不设置时仍会跟随位置。

扩展属性名按不区分大小写读取。没有加载本 C# 扩展的客户端会由原版忽略这些
未知属性，效果安全退化为普通粒子。

## 物品式绘制层级

`itemlayerdepth` 只改变绘制位置，不改变粒子坐标、速度、碰撞、寿命或对象池行为。
它在主场景的两个批次边界插入绘制：

```text
后层物品与结构 -> Depth > 0.5 粒子 -> 人物
人物 -> Depth <= 0.5 粒子 -> 前层物品与结构
```

每一层均先按原版创建顺序绘制 `AlphaBlend`，再按原版创建顺序绘制 `Additive`。
粒子编辑器、菜单及非 `GameScreen.DrawMap` 的绘制调用不会被过滤。

## OnSecondaryUse 约束

魔法书应显式使用 `requireaimtosecondaryuse="true"`，并把等待法阵放在
`OnSecondaryUse` 内。该状态效果不要设置 `duration` 或 `interval`：

- 按住瞄准时，原版每个模拟帧调用发射器，粒子持续跟随；
- 发射射弹的 `OnUse` 不会中断仍被按住的 `OnSecondaryUse`；
- 松开瞄准、丢弃或切换物品后，首个没有调用发射器的模拟帧开始淡出；
- 淡出期间重新瞄准会先清除旧粒子、复位原版三个发射计时器，再生成新粒子。

“每次举起只生成一个粒子”依赖 `particleamount="1"`、
`particlespersecond="0"` 和足够大的 `emitinterval`。粒子 prefab 自身可以有很长
的 `LifeTime` 和循环动画，停止仍由本扩展提前淡出。

完整但未注册的结构示例位于
`Examples/magic_book_secondary_use.xml`。它只是文档片段，不会被游戏加载。

## 已知边界

- 第一版以“发射器本模拟帧是否被调用”为唯一活跃依据，不读取键盘，也不直接
  检测 `Character.CanAim` 或 Holdable 的视觉姿势。
- 正式支持物品 StatusEffect 的单目标发射器。共享角色效果、炮塔专用发射器、
  一个发射器同时作用于多个目标以及粒子子发射器不作保证。
- 远端玩家使用原版已经同步到客户端的 Aim/物品状态；本扩展不注册网络消息、
  不发送 RPC，也不负责同步未被原版同步的自定义动画状态。
- 游戏暂停时 `GameScreen.Update` 不执行，心跳判断和淡出计时都会暂停。

## 失效保护

启动时会一次性核对目标方法、重载和私有字段。物品式绘制还会核对 `DrawMap`
中三次 `DrawBack`、一次 `DrawFront`、`Particle.Draw`、活动 Sprite 索引和粒子创建
顺序集合。分层兼容检查或运行时绘制失败时，只停用 `itemlayerdepth` 并让后续帧
退回原版；不会关闭 `followemitter`。跟随功能自身异常时仍会恢复正在淡出的粒子
颜色、清空跟随绑定并停用跟随功能，避免错误扩散到普通粒子。

## 本地验证

在 PowerShell 中运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tests\validate.ps1
```

该检查验证包清单、客户端边界、示例结构、禁止的网络调用、核心角度/潜艇局部
坐标数学，以及八个法阵发射器的分层属性。最终仍需在游戏中按验收矩阵验证
遮挡、混合、照明、对象池和多人表现。
