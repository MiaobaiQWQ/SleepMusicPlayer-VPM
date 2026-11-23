// 歌词大屏流体光晕背景着色器：通过硬件 MipMap 预滤波 + 36 点黄金角采样彻底消除封面文字重影。
Shader "SleepSanctuary/GPU/MusicAmbientBackdrop"
{
    Properties
    {
        _MainTex ("解码封面纹理", 2D) = "black" {}
        _HasCover ("封面淡入权重", Range(0, 1)) = 0
        _BaseTint ("暗炭底色", Color) = (0.065, 0.068, 0.085, 1)
        _WarmGlow ("暖琥珀点缀色", Color) = (0.92, 0.64, 0.36, 1)
        _BlurRadius ("流体模糊半径", Range(0.05, 0.35)) = 0.24
        _MipLevel ("背景采样 Mip 层级", Range(3.0, 7.0)) = 6.0
        _DimFactor ("护眼压暗系数", Range(0.1, 0.7)) = 0.36
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Cull Off ZWrite Off Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            sampler2D _MainTex;
            float _HasCover;
            float4 _BaseTint;
            float4 _WarmGlow;
            float _BlurRadius;
            float _MipLevel;
            float _DimFactor;

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 uv = i.uv;

                // 轻柔的流体域扭曲，让封面色彩自然呼吸流动
                float t = _Time.y * 0.12;
                float2 warp = float2(
                    sin(uv.y * 3.14159 + t) * 0.06 + cos(uv.x * 2.4 - t * 0.7) * 0.04,
                    cos(uv.x * 3.14159 - t * 0.8) * 0.06 + sin(uv.y * 2.4 + t) * 0.04
                );
                float2 suv = saturate(uv * 0.68 + 0.16 + warp);

                // 高阶 MipMap + 36 点黄金角圆盘采样：连续平滑融合，杜绝文字封面叠字重影
                float3 acc = float3(0, 0, 0);
                float wSum = 0.0;
                for (int k = 0; k < 36; k++)
                {
                    float fk = (float)k;
                    float fracK = (fk + 0.5) / 36.0;
                    float rad = sqrt(fracK) * _BlurRadius;
                    float ang = fk * 2.39996323;
                    float2 tapUV = saturate(suv + float2(cos(ang), sin(ang)) * rad);
                    float w = exp(-2.2 * fracK);

                    float3 cFine = tex2Dlod(_MainTex, float4(tapUV, 0.0, _MipLevel)).rgb;
                    float3 cAura = tex2Dlod(_MainTex, float4(tapUV, 0.0, _MipLevel + 1.2)).rgb;
                    acc += lerp(cFine, cAura, 0.45) * w;
                    wSum += w;
                }
                acc /= max(wSum, 0.0001);

                // 柔和压缩亮度极值并适度提纯色相
                float luma = dot(acc, float3(0.299, 0.587, 0.114));
                float softLuma = smoothstep(0.0, 1.0, luma) * 0.75 + 0.08;
                acc = saturate(softLuma + (acc - luma) * 1.35);

                // 无封面时的暖光待机渐变
                float2 p = uv - float2(0.28, 0.52);
                float warmBlob = exp(-dot(p * float2(1.4, 2.2), p * float2(1.4, 2.2)) * 2.2);
                float3 idleCol = _BaseTint.rgb + _WarmGlow.rgb * warmBlob * 0.14;

                float3 artCol = lerp(_BaseTint.rgb, acc * _DimFactor + _WarmGlow.rgb * 0.035, 0.86);
                float3 col = lerp(idleCol, artCol, _HasCover);

                // 四周暗角与右侧歌词区压暗，确保文字高对比清晰可读
                float vig = smoothstep(1.15, 0.25, length((uv - 0.5) * float2(1.1, 1.3)));
                float rightDark = lerp(1.0, 0.68, smoothstep(0.30, 0.62, uv.x));
                col *= lerp(0.55, 1.0, vig) * rightDark;

                // GPU SDF 圆角外框与细腻磨砂描边
                float2 q = abs(uv - 0.5) - float2(0.482, 0.468);
                float d = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - 0.018;
                float alpha = smoothstep(0.004, -0.004, d);
                col += float3(0.95, 0.90, 0.84) * smoothstep(0.005, 0.0, abs(d)) * 0.18;

                return fixed4(col, alpha * 0.98);
            }
            ENDCG
        }
    }
}
