// A dim reference photograph keeps peripheral context while the live centre fades into it.
Shader "ImmerseTheBay/ImmersiveVisko"
{
    Properties
    {
        _MainTex ("Live video", 2D) = "black" {}
        _EnvironmentTex ("Starting photograph", 2D) = "black" {}
        _EnvironmentDim ("Peripheral brightness", Range(0, 1)) = 0.36
        _HasVideo ("Live frame available", Float) = 0
        _SceneOpacity ("Scene opacity", Range(0, 1)) = 1
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        Cull Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            sampler2D _MainTex, _EnvironmentTex;
            float4 _MainTex_ST, _EnvironmentTex_ST;
            float _EnvironmentDim, _HasVideo, _SceneOpacity;
            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };
            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                // Native BGRA video uses the existing texture flip; JPEG photos do not.
                fixed3 photo = tex2D(_EnvironmentTex, TRANSFORM_TEX(i.uv, _EnvironmentTex)).rgb;
                fixed3 video = tex2D(_MainTex, TRANSFORM_TEX(i.uv, _MainTex)).rgb;
                float radius = length((i.uv - float2(0.5, 0.53)) / float2(0.43, 0.49));
                float centre = 1.0 - smoothstep(0.34, 1.0, radius);
                fixed3 focus = lerp(photo, video, saturate(_HasVideo));
                float2 margins = min(i.uv, 1.0 - i.uv);
                // Apply a second eased falloff to the entire photo/video composite.
                // This widens the transparent border and fades the corners before
                // the physical quad ends, including over bright real surfaces.
                float2 falloff = smoothstep(float2(0.015, 0.02), float2(0.24, 0.27), margins);
                float edge = falloff.x * falloff.y;
                edge *= edge;
                return fixed4(lerp(photo * _EnvironmentDim, focus, centre), edge * _SceneOpacity);
            }
            ENDCG
        }
    }
}
