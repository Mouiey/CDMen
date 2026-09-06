# Emitter Follow Client

面向 Barotrauma 1.13.4.0 / LuaCs 的纯客户端粒子扩展。它给原版
`ParticleEmitter` 增加可选的父子变换和“心跳停止后淡出”行为，不修改游戏 DLL，
也不创建任何隐藏物品、射弹或状态效果。

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
- `copyentityangle="true"` 让原版把物品方向传给发射器，因此旋转、贴图角度
  和速度方向才能随物品变化；不设置时仍会跟随位置。

属性名按不区分大小写读取。没有加载本 C# 扩展的客户端会由原版忽略这两个
未知属性，效果退化为普通粒子。

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

启动时会一次性核对目标方法、重载和私有字段。如果当前游戏版本不兼容，扩展
会记录一条错误并完全禁用，保留原版粒子行为。运行中若补丁发生异常，也会恢复
正在淡出的粒子颜色、清空绑定并停用扩展，避免错误扩散到普通粒子。

## 本地验证

在 PowerShell 中运行：

```powershell
pwsh -File .\Tests\validate.ps1
```

该检查验证包清单、客户端边界、示例结构、禁止的网络调用以及核心角度/潜艇
局部坐标数学。最终仍需在游戏中按验收矩阵验证绘制、对象池和多人表现。
