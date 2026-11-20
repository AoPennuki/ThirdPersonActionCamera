# Third Person Action Camera

![FF14 对战资料图标](https://xivapi.com/i/000000/000053.png)

FF14 第三人称动作镜头。鼠标直控镜头，中央准星选目标，按住右键进行 1.65× 平滑变焦。插件加载后默认关闭。

1. 从 [GitHub Releases](https://github.com/AoPennuki/ThirdPersonActionCamera/releases) 下载并解压测试包，在 Dalamud 的开发插件设置中添加 `ThirdPersonActionCamera.dll`。
2. 切到第三人称，输入 `/acti on` 开启。默认右肩，角色出现在准星左侧，移动鼠标可转动镜头。用 `/acti left`、`/acti right` 或 `/acti center` 切换构图。
3. 直接按普通目标技能，由原生选取在准星位置找目标。准星下没有合法目标时，这次技能不发出。自用技能、地面技能和已有宏按各自原规则处理，已经排队的技能保留当时选定的目标。
4. 按住 **鼠标右键**触发所选版本的放大，松开恢复。用 `/acti zoom 2` 可临时调整倍率。
5. 按住 **Alt** 可以临时操作光标。输入 `/acti off` 关闭，`/acti status` 查看状态。

v0.5.3 开始提供 `/acti` 短命令。原来的 `/actioncam` 仍可以使用。

游戏聊天或 Dalamud 文本输入、切换到第一人称、过图、过场、拍照和失去焦点时会释放鼠标。验证第三人称时，可以先单独加载这一个镜头插件，便于定位问题。

## 三个版本

| 版本 | 右键效果 |
| --- | --- |
| v0.4.1 | 沿原版视场角将镜头向人物前移，右肩状态下同时往左收，原版镜头距离足够时约 3×。 |
| v0.5.1 | 1.65× 焦段变焦，光轴朝人物一侧偏转。 |
| v0.5.2 / v0.5.3 | 1.65× 纯焦段变焦，肩位和取景方向保持固定。 |

两个 1.65× 版本使用相同的缓动。按住和松开时平滑起停，中途反向会承接当前速度。三个包使用同一个插件标识，每次加载一个版本。

## 验证状态

测试包基于国服 Dalamud API 15。视场角、肩位构图和技能分流已通过离线检查。不同客户端版本和多个镜头插件同时启用的兼容性有待验证。

游戏相机的视场角字段参考 [Cammy 的视场角设置](https://github.com/UnknownX7/Cammy/blob/master/PresetManager.cs#L37-L43)。

## 构建

需要 .NET 10 SDK 和 Dalamud API 15 的程序集。在仓库根目录运行以下命令。把 `DalamudLibPath` 指向包含 `Dalamud.dll` 的实际目录。

```powershell
dotnet build ThirdPersonActionCamera.csproj -c Release '-p:DalamudLibPath=C:/path/to/Dalamud'
```

默认构建 v0.5.3 纯焦段。使用 `-p:CameraVariant=Dolly` 或 `-p:CameraVariant=LensWithPan` 可构建另外两个对照版。

图标取自 FF14「对战资料」菜单（编号 53），通过 XIVAPI 加载。许可和署名见 [LICENSE.md](LICENSE.md) 与 [NOTICE.md](NOTICE.md)。
