// 歌词单行 GPU 逐字扫光上色着色器：支持逐字暖金推进、正在演唱字符微上浮与柔和泛光。
Shader "SleepSanctuary/GPU/LyricLineSweep"
{
    Properties
    {
        [PerRendererData] _MainTex ("字体纹理", 2D) = "white" {}
        _Color ("基础色调", Color) = (1, 1, 1, 1)
        _ActiveColor ("已唱暖金色", Color) = (0.98, 0.78, 0.50, 1)
        _IdleColor ("未唱珍珠白", Color) = (0.86, 0.84, 0.80, 0.78)
        _Sweep ("逐字上色进度 (0..1)", Range(0, 1)) = 1.0
        _TextWidth ("当前句实际宽度 (px)", Range(120, 900)) = 520.0
        _FlipFade ("切行上浮渐显 (0..1)", Range(0, 1)) = 1.0
        _IsActive ("是否当前高亮句", Range(0, 1)) = 1.0
        _FloatHeight ("逐字上浮高度 (px)", Range(0, 8)) = 4.2
    }

    SubShader
    {
        Tags { "Queue"="Transparent+2" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Plane" }
        Cull Off Lighting Off ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color  : COLOR;
                float2 uv     : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos    : SV_POSITION;
                fixed4 color  : COLOR;
                float2 uv     : TEXCOORD0;
                float  localX : TEXCOORD1;
            };

            sampler2D _MainTex;
            fixed4 _Color;
            fixed4 _ActiveColor;
            fixed4 _IdleColor;
            float _Sweep;
            float _TextWidth;
            float _FlipFade;
            float _IsActive;
            float _FloatHeight;

            v2f vert(appdata v)
            {
                v2f o;
                // 按当前行实际像素宽度归一化水平坐标（860px 容器左边界为 -430）
                float localX = saturate((v.vertex.x + 430.0) / max(_TextWidth, 120.0));

                float3 p = v.vertex.xyz;
                // 切行时整体丝滑上浮
                p.y -= (1.0 - _FlipFade) * 14.0;
                // Apple Music 风格逐字上浮：刚被扫到的字符轻柔抬起再落回
                float activeWindow = step(0.004, _Sweep) * step(_Sweep, 0.996) * _IsActive;
                float lift = exp(-pow((localX - _Sweep) * 7.5, 2.0)) * _FloatHeight * activeWindow;
                p.y += lift;

                o.pos = UnityObjectToClipPos(float4(p, 1.0));
                o.uv = v.uv;
                o.color = v.color * _Color;
                o.localX = localX;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed glyphAlpha = tex2D(_MainTex, i.uv).a;
                // 精准逐字暖金扫光边界
                float sweepMask = smoothstep(_Sweep + 0.022, _Sweep - 0.022, i.localX);
                fixed3 activeRgb = lerp(_IdleColor.rgb, _ActiveColor.rgb, sweepMask);

                // 当前正在演唱字符的细腻柔光高亮
                float activeWindow = step(0.004, _Sweep) * step(_Sweep, 0.996);
                float wordGlow = exp(-pow((i.localX - _Sweep) * 14.0, 2.0)) * 0.22 * activeWindow;
                activeRgb = saturate(activeRgb + wordGlow);

                fixed3 rgb = lerp(i.color.rgb, activeRgb, _IsActive);
                return fixed4(rgb, glyphAlpha * i.color.a * _FlipFade);
            }
            ENDCG
        }
    }
}
