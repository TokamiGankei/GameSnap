基于 TokamiGankei 开发的 [GameSnap](https://github.com/TokamiGankei/GameSnap) 原版项目，此修复程序提高了匹配准确率，增加了评分算法，增加了黑名单机制，细分文件夹创建选项，增加截图完成后的通知，增加异常通知，并完成了完整的中文本地化。此外，它还调整了使用 [screenshotsvisualizer_Fixed](https://github.com/ERROR0cai/screenshotsvisualizer_Fixed) 的 API 调用方法。

### 新增功能
- 新增黑名单前缀机制，匹配的文件将被完全忽略
- 细分文件夹创建选项，可分别控制「游戏启动时创建」和「截图需要时创建」
- 新增截图整理后的通知选项，支持「每次截图整理后」和「游戏结束后」两种通知时机
- 新增图片分类异常通知，当游戏名与目标文件夹不匹配或移动失败时显示警告

### 优化改进
- 完善中英文本地化支持，还可自行翻译更多语言
- 改进相似度分数显示，明确展示游戏名与文件夹的匹配关系

### 修复
- 修复修改设置后当前游戏状态丢失的问题
- 修复调用 screenshotsvisualizer API 进行刷新却无效的问题


<img width="611" height="996" alt="image" src="https://github.com/user-attachments/assets/505898b7-c9d1-4ded-84f0-a6e43f2dd157" />
<img width="383" height="219" alt="image" src="https://github.com/user-attachments/assets/3b0df3e7-5f29-4e6a-8209-e52ccfd17426" />
