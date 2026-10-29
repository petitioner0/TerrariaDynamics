# 固定方块纹理生成记录

使用内置 imagegen，参考 `Content/Tiles/YardThrusterTile.png` 与
`Content/Tiles/YardThrusterTile_Active.png` 的金属边框和青色像素风格。
生成左右排列的两状态图，再拆分并最近邻采样到 16×16；方块添加透明帧间距至 18×18。

最终资源：

- `Content/Tiles/YardAnchorTile.png`：未激活。
- `Content/Tiles/YardAnchorTile_Active.png`：激活。
- `Content/Items/YardAnchor.png`：16×16 物品图标。

生成提示词：

```text
Create a production pixel-art sprite sheet for Terraria: two square solid mechanical anchor blocks, inactive on LEFT and active on RIGHT. Use the two supplied tiny thruster sprite sheets ONLY as style references: dark blue-gray outline, simple steel blue casing, pale metallic highlights, cyan indicator. This is a NEW block, not a thruster. Image must be 2:1 aspect ratio, filled edge-to-edge by two equal square sprites with no margins and no gap. Design on a strict 32 columns by 16 rows pixel grid: each block is EXACTLY 16x16 logical pixels. Render each logical pixel as a large uniform square, no antialiasing, no fine detail, no gradients, no text, no labels, no background. Each sprite is an opaque full square block, outer 1 logical-pixel dark navy border, bevelled steel frame with four tiny corner rivets, dark inset panel and a very simple central squared clamp/lock symbol. The two blocks have IDENTICAL silhouette and geometry; inactive center symbol muted slate teal, active center symbol bright cyan with pale cyan core and two tiny amber status pixels. Keep design readable at 16x16. Return just this two-frame sheet. Reference images are for palette and pixel simplicity only.
```
