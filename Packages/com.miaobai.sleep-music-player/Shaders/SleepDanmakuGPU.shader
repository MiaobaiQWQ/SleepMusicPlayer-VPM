Shader "SleepSanctuary/DanmakuGPU"
{
    Properties
    {
        [PerRendererData] _MainTex ("Font Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _OutlineColor ("Outline Color", Color) = (0,0,0,0.88)
        _OutlineWidth ("Outline Pixel Width", Range(0.5, 3.0)) = 1.6
        _DanmakuClock ("Danmaku Clock", Float) = 0
        _ClipRect ("Clip Rect", Vector) = (-960, -540, 960, 540)
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
            "CanUseSpriteAtlas" = "True"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata_t
            {
                float4 vertex   : POSITION;
                float4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex    : SV_POSITION;
                fixed4 color     : COLOR;
                float2 texcoord  : TEXCOORD0;
                float2 canvasPos : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            fixed4 _Color;
            fixed4 _OutlineColor;
            float _OutlineWidth;
            float _DanmakuClock;
            float4 _ClipRect;

            // 每槽位 GPU 运动参数（最多 80 条同屏，单 DrawCall 批处理，零每帧 Canvas 重建）：
            // x = startX (Canvas px), y = speed (px/sec), z = spawnClock (sec), w = isScroll (1=滚动, 0=固定)
            float4 _SlotAnim[80];

            v2f vert(appdata_t v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                // 从静止槽位 Z 编码还原槽位索引（每槽步长 0.05 Canvas 单位 = 0.05mm，随后归零防止深度冲突）
                int slotIdx = clamp((int)round(v.vertex.z * 20.0), 0, 79);
                float4 anim = _SlotAnim[slotIdx];
                float elapsed = max(0.0, _DanmakuClock - anim.z);

                float4 pos = v.vertex;
                pos.x += (anim.x - anim.y * elapsed) * anim.w;
                pos.z = 0.0;

                o.canvasPos = pos.xy;
                o.vertex = UnityObjectToClipPos(pos);
                o.texcoord = v.texcoord;
                o.color = v.color * _Color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // GPU 硬件边缘裁剪，替代高开销 RectMask2D 动态重算
                if (i.canvasPos.x < _ClipRect.x || i.canvasPos.x > _ClipRect.z ||
                    i.canvasPos.y < _ClipRect.y || i.canvasPos.y > _ClipRect.w)
                {
                    discard;
                }

                float centerA = tex2D(_MainTex, i.texcoord).a;

                // 4-tap GPU 片段描边，彻底替代 UnityEngine.UI.Outline（节省 80% 顶点数）
                float2 off = _MainTex_TexelSize.xy * _OutlineWidth;
                float outA = max(
                    max(tex2D(_MainTex, i.texcoord + float2( off.x,  off.y)).a,
                        tex2D(_MainTex, i.texcoord + float2(-off.x,  off.y)).a),
                    max(tex2D(_MainTex, i.texcoord + float2( off.x, -off.y)).a,
                        tex2D(_MainTex, i.texcoord + float2(-off.x, -off.y)).a)
                );

                float finalA = max(centerA, outA * _OutlineColor.a) * i.color.a;
                if (finalA < 0.01) discard;

                fixed3 rgb = lerp(_OutlineColor.rgb, i.color.rgb, saturate(centerA * 1.15));
                return fixed4(rgb, finalA);
            }
            ENDCG
        }
    }
}
