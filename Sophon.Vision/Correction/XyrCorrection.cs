using System;

namespace Sophon.Vision.Correction
{
    /// <summary>
    /// 对位纠偏目标位置结果。
    /// </summary>
    /// <param name="DeltaX">平台 X 轴应移动的补偿量 (mm)</param>
    /// <param name="DeltaY">平台 Y 轴应移动的补偿量 (mm)</param>
    /// <param name="DeltaAngle">平台 R 轴应旋转的补偿角 (deg)</param>
    /// <param name="CorrectedX">补偿后的绝对目标物理位置 X (mm)</param>
    /// <param name="CorrectedY">补偿后的绝对目标物理位置 Y (mm)</param>
    public record CorrectionTarget(
        double DeltaX,
        double DeltaY,
        double DeltaAngle,
        double CorrectedX,
        double CorrectedY);

    /// <summary>
    /// XYR (UVW / 对位模组) 对位平台旋转中心补偿算法。
    /// 
    /// 数学推导与原理：
    /// 工业对位平台（如 XYR、UVW 平台）通常由平移轴与旋转轴机械组成。
    /// 当机构绕旋转中心 (cx, cy) 旋转角度 Δθ 时，目标特征点 P(x, y) 不仅产生纯角度变化，
    /// 还会以 (cx, cy) 为圆心产生伴随旋转弧线平移偏移量：
    /// 
    /// 设物料当前测量物理位置为 P(px, py)，基准目标位置为 P0(x0, y0)。
    /// 此时物体不仅有相对偏差 Δpx = px - x0, Δpy = py - y0，
    /// 还有角度偏差 Δθ = θ - θ0。
    /// 机构在纠偏时，需要反向旋转 -Δθ，将工件姿态扶正。
    /// 但当工件以机构旋转中心 C(cx, cy) 反向旋转 -Δθ 时，工件点 P 也会绕 C 旋转移动到新位置 P'：
    /// 
    /// [ x' - cx ]   [ cos(-Δθ)  -sin(-Δθ) ] [ px - cx ]
    /// [ y' - cy ] = [ sin(-Δθ)   cos(-Δθ) ] [ py - cy ]
    /// 
    /// 旋转后的位置 P'(x', y') 距基准点 P0(x0, y0) 仍然残存平移偏差：
    /// 最终平移补偿量：
    /// CompensateX = x0 - x'
    /// CompensateY = y0 - y'
    /// CompensateAngle = -Δθ
    /// 
    /// 若采用运动控制平台相对补偿量（Delta 驱动）：
    /// DeltaX = x0 - x'
    /// DeltaY = y0 - y'
    /// DeltaAngle = -Δθ
    /// 机构执行此组运动后，工件特征点将完美重合到标准基准 P0 上，且角度姿态归零。
    /// </summary>
    public static class XyrCorrection
    {
        /// <summary>
        /// 计算 XYR 平台的对位补偿量及修正后目标坐标。
        /// </summary>
        /// <param name="currentWorldX">工件当前物理位置 X (mm)</param>
        /// <param name="currentWorldY">工件当前物理位置 Y (mm)</param>
        /// <param name="currentAngleDeg">工件当前物理角度 (deg)</param>
        /// <param name="targetWorldX">期望基准物理位置 X (mm)</param>
        /// <param name="targetWorldY">期望基准物理位置 Y (mm)</param>
        /// <param name="targetAngleDeg">期望基准物理角度 (deg)</param>
        /// <param name="rotationCenterX">对位平台旋转中心物理 X (mm)</param>
        /// <param name="rotationCenterY">对位平台旋转中心物理 Y (mm)</param>
        /// <returns>纠偏输出结果</returns>
        public static CorrectionTarget Calculate(
            double currentWorldX,
            double currentWorldY,
            double currentAngleDeg,
            double targetWorldX,
            double targetWorldY,
            double targetAngleDeg,
            double rotationCenterX,
            double rotationCenterY)
        {
            // 角度差值 (当前 - 目标)
            double deltaAngleDeg = currentAngleDeg - targetAngleDeg;

            // 为纠正角度，机构需旋转 -deltaAngleDeg
            double rotateAngleRad = -deltaAngleDeg * Math.PI / 180.0;
            double cos = Math.Cos(rotateAngleRad);
            double sin = Math.Sin(rotateAngleRad);

            // 绕旋转中心 (cx, cy) 旋转工件当前坐标 (currentWorldX, currentWorldY)
            double dx = currentWorldX - rotationCenterX;
            double dy = currentWorldY - rotationCenterY;

            double rotatedX = rotationCenterX + (dx * cos - dy * sin);
            double rotatedY = rotationCenterY + (dx * sin + dy * cos);

            // 扶正旋转后，距离目标基准的平移差（机构需要补的平移量）
            double deltaX = targetWorldX - rotatedX;
            double deltaY = targetWorldY - rotatedY;
            double deltaAngle = -deltaAngleDeg;

            // 最终对位纠偏后特征点在物理坐标系下的绝对目标位置 (rotated + delta = target)
            double correctedX = rotatedX + deltaX;
            double correctedY = rotatedY + deltaY;

            return new CorrectionTarget(
                DeltaX: deltaX,
                DeltaY: deltaY,
                DeltaAngle: deltaAngle,
                CorrectedX: correctedX,
                CorrectedY: correctedY
            );
        }

        /// <summary>
        /// 基于示教基准与视觉测量偏差量便捷计算纠偏量。
        /// </summary>
        public static CorrectionTarget CalculateFromOffset(
            VisionTeachBase teachBase,
            VisionOffset offset,
            double rotationCenterX,
            double rotationCenterY)
        {
            if (teachBase == null) throw new ArgumentNullException(nameof(teachBase));
            if (offset == null) throw new ArgumentNullException(nameof(offset));

            double currentX = teachBase.TeachWorldX + offset.DeltaWorldX;
            double currentY = teachBase.TeachWorldY + offset.DeltaWorldY;
            double currentAngle = teachBase.TeachWorldAngleDeg + offset.DeltaAngleDeg;

            return Calculate(
                currentWorldX: currentX,
                currentWorldY: currentY,
                currentAngleDeg: currentAngle,
                targetWorldX: teachBase.TeachWorldX,
                targetWorldY: teachBase.TeachWorldY,
                targetAngleDeg: teachBase.TeachWorldAngleDeg,
                rotationCenterX: rotationCenterX,
                rotationCenterY: rotationCenterY
            );
        }
    }
}
