// 歌词单行 GPU 逐字扫光上色着色器：基于 Canvas 真实左边界与渲染宽度实现精准逐字暖金推进与微上浮。
Shader "SleepSanctuary/GPU/LyricLineSweep"
{
    Properties
    {
        [PerRendererData] _MainTex ("字体纹理", 2D) = "white" {}
        _Color ("基础色调", Color) = (1, 1, 1, 1)
        _ActiveColor ("已唱暖金色", Color) = (0.98, 0.78, 0.50, 1)
        _IdleColor ("未唱珍珠白", Color) = (0.86, 0.84, 0.80, 0.78)
        _Sweep ("逐字上色进度 (0..1)", Range(0, 1)) = 1.0
        _StartX ("文本左边界 Canvas X (px)", Float) = -162.0
        _TextWidth ("当前句实际宽度 (px)", Range(40, 3200)) = 520.0
        _FlipFade ("切行上浮渐显 (0..1)", Range(0, 1)) = 1.0
        _IsActive ("是否当前高亮句", Range(0, 1)) = 1.0
        _FloatHeight ("逐字上浮高度 (px)", Range(0, 10)) = 5.5
        _CurveAngle ("曲面屏总弧度 (rad)", Range(-1.58, 1.58)) = 0.0
        _StageWidth ("舞台画布宽度 (px)", Float) = 1920.0
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
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos    : SV_POSITION;
                fixed4 color  : COLOR;
                float2 uv     : TEXCOORD0;
                float  localX : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            fixed4 _Color;
            fixed4 _ActiveColor;
            fixed4 _IdleColor;
            float _Sweep;
            float _StartX;
            float _TextWidth;
            float _FlipFade;
            float _IsActive;
            float _FloatHeight;
            float _CurveAngle;
            float _StageWidth;

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                // Unity UI Canvas 网格顶点位于 Canvas 本地坐标系，减去真实左边界 _StartX 后再除以真实字宽
                float localX = saturate((v.vertex.x - _StartX) / max(_TextWidth, 40.0));

                float3 p = v.vertex.xyz;
                // 切行时轻快微上浮
                p.y -= (1.0 - _FlipFade) * 10.0;
                // Apple Music 风格逐字上浮：刚被扫到的字符轻柔抬起再落回
                float activeWindow = step(0.004, _Sweep) * step(_Sweep, 0.996) * _IsActive;
                float lift = exp(-pow((localX - _Sweep) * 7.5, 2.0)) * _FloatHeight * activeWindow;
                p.y += lift;

                // 曲面屏 GPU 顶点弯曲（_CurveAngle = 0 时完全退化为平面）
                if (abs(_CurveAngle) > 0.001)
                {
                    float halfW = max(100.0, _StageWidth * 0.5);
                    float halfAng = abs(_CurveAngle) * 0.5;
                    float radius = halfW / max(0.001, halfAng);
                    float sgn = _CurveAngle >= 0.0 ? 1.0 : -1.0;
                    float theta = (p.x / halfW) * halfAng;
                    p.x = radius * sin(theta);
                    p.z -= sgn * radius * (1.0 - cos(theta));
                }

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
                float wordGlow = exp(-pow((i.localX - _Sweep) * 14.0, 2.0)) * 0.24 * activeWindow;
                activeRgb = saturate(activeRgb + wordGlow);

                fixed3 rgb = lerp(i.color.rgb, activeRgb, _IsActive);
                return fixed4(rgb, glyphAlpha * i.color.a * _FlipFade);
            }
            ENDCG
        }
    }
}
