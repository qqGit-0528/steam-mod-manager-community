---
title: "运行配置依赖检查与确认下载"
date_created: 2026-11-08
owner: "ProfileDependencyService"
---

# 背景与目标

第二批扩展运行配置的诊断和导入预览。

## 1. 范围与非目标

从运行配置启用项展开本地名称和工坊项目 ID 依赖，展示缺失、歧义、未启用和循环。

## 2. 需求与验收条件

菱形重复引用只排队一次。可用空工坊声明与未知分开显示。缺失的同游戏工坊物品先预览再排队，切换配置或安装变化使预览失效。未确认时保持只读。

## 3. 责任模块与实现步骤

Steam 公共物品页仅用于识别 Required Items，元数据仍由 WorkshopService 校验游戏和 ID。Application 遍历有界依赖图，使用现有 WorkshopDownloadService 排队。无应用内下载时提供 Steam 入口。

## 4. 契约、依赖与兼容性

兼容现有配置与调用方。操作绑定当前游戏工作区。

## 5. 测试策略

覆盖正向流程、数据变化、缺失、解析或请求失败及取消。按 [协作规范](../CONTRIBUTING.md#4-必须完成的检查) 执行门禁。

## 6. 审查与执行结果

真实只读探针从 2996881191 取得两个前置，2220098919 得到空列表，物品 1 返回未知。工坊元数据与依赖页分别核对游戏和项目身份。解析上限 128 项。桌面测试验证预览阶段不创建下载、离开后预览清除、明确确认后菱形共用的前置只排一次。覆盖本地缺失、禁用、多副本和循环。真实订阅及下载以测试替身验收。

[Steamworks 依赖说明](https://partner.steamgames.com/doc/api/isteamugc#AddDependency) · [工坊物品样例](https://steamcommunity.com/workshop/filedetails/?id=2996881191&l=english)。

macOS arm64 还原、Release 构建、全量 603 项测试及 `git diff --check` 通过。截图、探针日志和 TRX 位于本地 `artifacts/batch2/`。

## 7. 相关资料

[架构约定](../docs/architecture.md#架构约定)。
