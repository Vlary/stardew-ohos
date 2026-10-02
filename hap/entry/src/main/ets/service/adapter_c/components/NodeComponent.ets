/*
 * Copyright (c) 2023 Huawei Device Co., Ltd.
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

import { ViewNodeController, NodeParams, NodeType } from '../common/Node';
import entry from 'libentry.so';

/* ArkTS MouseButton(Left=0,Right=1,Middle=2,Back=3,Forward=4) → SDL(左=1,中=2,右=3) */
function hmBtnToSdl(b: MouseButton): number {
  switch (b) {
    case MouseButton.Left: return 1;
    case MouseButton.Right: return 3;
    case MouseButton.Middle: return 2;
    case MouseButton.Back: return 4;
    case MouseButton.Forward: return 5;
    default: return 0;
  }
}

@Builder
export function WindowBuilderForView(params: NodeParams) {
  Stack({ alignContent: Alignment.TopStart }) {
    if (params?.node_type == NodeType.XComponent) {
      // XComponent
      XComponent({
        id: params?.node_xcomponent.id,
        type: params?.node_xcomponent.type,
        libraryname: params?.node_xcomponent.libraryname,
        controller: params?.node_xcomponent.controller
      })
        .width('100%')
        .height('100%')
        .focusable(true)
        .defaultFocus(true)
        .onLoad(params?.node_xcomponent.onLoad)
        .onDestroy(params?.node_xcomponent.onDestroy)
        .focusOnTouch(true)
        .onMouse((event: MouseEvent) => {
          if (event.action == MouseAction.Press) {
            entry.sendMouseButton(1, hmBtnToSdl(event.button))
          } else if (event.action == MouseAction.Release) {
            entry.sendMouseButton(0, hmBtnToSdl(event.button))
          } else if (event.action == MouseAction.Move || event.action == MouseAction.Hover) {
            entry.sendMouseMotion(event.x, event.y)
          }
        })
        .onAxisEvent((event: AxisEvent) => {
          if (event.action == AxisAction.UPDATE) {
            entry.sendMouseWheel(event.getHorizontalAxisValue(), event.getVerticalAxisValue())
          }
        })
        .onKeyEvent((event: KeyEvent) => {
          if (event.type == KeyType.Down) {
            entry.sendKey(1, event.keyCode)
          } else if (event.type == KeyType.Up) {
            entry.sendKey(0, event.keyCode)
          }
        })
        /* 输入法处理前的按键钩子：PC 上物理键盘默认派给 IME 通道，
           onKeyEvent（焦点链）不触发；PreIme 在 IME 之前收到原始键 */
        .onKeyPreIme((event: KeyEvent) => {
          if (event.type == KeyType.Down) {
            entry.sendKey(1, event.keyCode)
          } else if (event.type == KeyType.Up) {
            entry.sendKey(0, event.keyCode)
          }
          return false
        })
    } else if (params?.node_type == NodeType.UIExtension) {
      // @todo UIExtension is not support now
    }

    ForEach(params.node_list, (item: ViewNodeController) => {
      NodeContainer(item)
        .zIndex(item.getParams().z_index)
        .position({
          x: (Number.isNaN(Number(item.getParams().position_x.toString())) ? item.getParams().position_x : item.getParams().position_x + "px"),
          y: (Number.isNaN(Number(item.getParams().position_y.toString())) ? item.getParams().position_y : item.getParams().position_y + "px")
        })
        .width(Number.isNaN(Number(item.getParams().width.toString())) ? item.getParams().width : item.getParams().width + "px")
        .height(Number.isNaN(Number(item.getParams().height.toString())) ? item.getParams().height : item.getParams().height + "px")
        .borderColor(item.getParams().border_color)
        .borderWidth(item.getParams().border_width)
        .visibility(item.getParams().visibility)
    }, (_item: ViewNodeController, index: number) => index.toString())
  }
}