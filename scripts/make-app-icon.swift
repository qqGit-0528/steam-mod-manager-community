import AppKit
import CoreGraphics
import Foundation

// 生成 1024x1024 应用图标：深酒红圆角方块 + 暖白拼图块（Mod 符号）
// 用法：swift make-app-icon.swift <输出 png 路径>

let outputPath = CommandLine.arguments.count > 1
    ? CommandLine.arguments[1]
    : "AppIcon-1024.png"

let size = 1024
let inset: CGFloat = 100
let content = CGFloat(size) - inset * 2
let corner = content * 0.225
let colorSpace = CGColorSpaceCreateDeviceRGB()

guard let ctx = CGContext(
    data: nil,
    width: size,
    height: size,
    bitsPerComponent: 8,
    bytesPerRow: 0,
    space: colorSpace,
    bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
) else {
    FileHandle.standardError.write("无法创建绘图上下文\n".data(using: .utf8)!)
    exit(1)
}

ctx.setAllowsAntialiasing(true)
ctx.setShouldAntialias(true)

let bodyRect = CGRect(x: inset, y: inset, width: content, height: content)
let bodyPath = CGPath(
    roundedRect: bodyRect,
    cornerWidth: corner,
    cornerHeight: corner,
    transform: nil
)

// 背景
ctx.saveGState()
ctx.addPath(bodyPath)
ctx.clip()

if let gradient = CGGradient(
    colorsSpace: colorSpace,
    colors: [
        CGColor(red: 0.42, green: 0.08, blue: 0.13, alpha: 1.0),  // #6B1622
        CGColor(red: 0.16, green: 0.03, blue: 0.06, alpha: 1.0)   // #290810
    ] as CFArray,
    locations: [0.0, 1.0]
) {
    ctx.drawLinearGradient(
        gradient,
        start: CGPoint(x: inset, y: CGFloat(size) - inset),
        end: CGPoint(x: CGFloat(size) - inset, y: inset),
        options: []
    )
}

if let glow = CGGradient(
    colorsSpace: colorSpace,
    colors: [
        CGColor(red: 1.0, green: 1.0, blue: 1.0, alpha: 0.14),
        CGColor(red: 1.0, green: 1.0, blue: 1.0, alpha: 0.0)
    ] as CFArray,
    locations: [0.0, 1.0]
) {
    let center = CGPoint(x: CGFloat(size) / 2, y: CGFloat(size) - inset - content * 0.08)
    ctx.drawRadialGradient(
        glow,
        startCenter: center,
        startRadius: 0,
        endCenter: center,
        endRadius: content * 0.78,
        options: []
    )
}
ctx.restoreGState()

ctx.addPath(bodyPath)
ctx.setStrokeColor(CGColor(red: 1, green: 1, blue: 1, alpha: 0.10))
ctx.setLineWidth(3)
ctx.strokePath()

// 拼图块：左侧平直，顶部凹口，右侧凸耳，底部凹口
let s = content * 0.50
let x0 = (CGFloat(size) - s) / 2
let y0 = (CGFloat(size) - s) / 2

let puzzle = CGMutablePath()
puzzle.move(to: CGPoint(x: x0, y: y0))
puzzle.addLine(to: CGPoint(x: x0 + s * 0.36, y: y0))
puzzle.addCurve(
    to: CGPoint(x: x0 + s * 0.64, y: y0),
    control1: CGPoint(x: x0 + s * 0.42, y: y0 + s * 0.20),
    control2: CGPoint(x: x0 + s * 0.58, y: y0 + s * 0.20)
)
puzzle.addLine(to: CGPoint(x: x0 + s, y: y0))
puzzle.addLine(to: CGPoint(x: x0 + s, y: y0 + s * 0.36))
puzzle.addCurve(
    to: CGPoint(x: x0 + s, y: y0 + s * 0.64),
    control1: CGPoint(x: x0 + s * 1.20, y: y0 + s * 0.42),
    control2: CGPoint(x: x0 + s * 1.20, y: y0 + s * 0.58)
)
puzzle.addLine(to: CGPoint(x: x0 + s, y: y0 + s))
puzzle.addLine(to: CGPoint(x: x0 + s * 0.64, y: y0 + s))
puzzle.addCurve(
    to: CGPoint(x: x0 + s * 0.36, y: y0 + s),
    control1: CGPoint(x: x0 + s * 0.58, y: y0 + s * 0.80),
    control2: CGPoint(x: x0 + s * 0.42, y: y0 + s * 0.80)
)
puzzle.addLine(to: CGPoint(x: x0, y: y0 + s))
puzzle.closeSubpath()

// 阴影
ctx.saveGState()
ctx.setShadow(
    offset: CGSize(width: 0, height: -10),
    blur: 30,
    color: CGColor(red: 0, green: 0, blue: 0, alpha: 0.30)
)
ctx.addPath(puzzle)
ctx.setFillColor(CGColor(red: 0.97, green: 0.94, blue: 0.89, alpha: 1.0))
ctx.fillPath()
ctx.restoreGState()

// 暖白渐变
ctx.saveGState()
ctx.addPath(puzzle)
ctx.clip()
if let fill = CGGradient(
    colorsSpace: colorSpace,
    colors: [
        CGColor(red: 1.0, green: 0.99, blue: 0.97, alpha: 1.0),   // #FFFDF7
        CGColor(red: 0.93, green: 0.88, blue: 0.80, alpha: 1.0)   // #EDE0CC
    ] as CFArray,
    locations: [0.0, 1.0]
) {
    ctx.drawLinearGradient(
        fill,
        start: CGPoint(x: x0, y: y0 + s),
        end: CGPoint(x: x0, y: y0),
        options: []
    )
}
ctx.restoreGState()

guard let image = ctx.makeImage() else {
    FileHandle.standardError.write("无法生成图像\n".data(using: .utf8)!)
    exit(1)
}
let rep = NSBitmapImageRep(cgImage: image)
guard let png = rep.representation(using: .png, properties: [:]) else {
    FileHandle.standardError.write("无法编码 PNG\n".data(using: .utf8)!)
    exit(1)
}
try png.write(to: URL(fileURLWithPath: outputPath))
print("已生成 \(outputPath)")
