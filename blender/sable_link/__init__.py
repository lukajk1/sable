# SPDX-License-Identifier: GPL-3.0-or-later
"""Sable Link: live-link selected Blender meshes into Sable for texture painting.

"Send to Sable" exports the selected meshes to a link folder (model.glb + link.json) and starts Sable with
``--link <link.json>``. While linked, geometry/transform/material edits re-export after a short pause, and image
files Sable saves are reloaded in Blender.
"""

import os
import time

import bpy
from bpy.props import StringProperty

from . import link


class SABLE_AP_preferences(bpy.types.AddonPreferences):
    bl_idname = __package__

    sable_path: StringProperty(
        name="Sable.exe",
        description="Path to Sable.exe. Leave empty to use the installed Sable",
        subtype='FILE_PATH',
        default="",
    )

    def draw(self, context):
        layout = self.layout
        layout.prop(self, "sable_path")
        found = link.find_sable_exe(self.sable_path)
        layout.label(text="Using: " + found if found else "Sable.exe not found; set the path above",
                     icon='CHECKMARK' if found else 'ERROR')


def _prefs(context):
    addon = context.preferences.addons.get(__package__)
    return addon.preferences if addon else None


class SABLE_OT_send_selection(bpy.types.Operator):
    """Link the selected meshes to Sable: export them and start Sable if it isn't running"""
    bl_idname = "sable.send_selection"
    bl_label = "Send to Sable"
    bl_options = {'REGISTER'}  # No 'UNDO': linking never changes the scene.

    def execute(self, context):
        objects = [obj for obj in context.selected_objects if obj.type == 'MESH']
        if not objects:
            self.report({'ERROR'}, "Select one or more mesh objects to send to Sable")
            return {'CANCELLED'}

        need_launch = not link.sable_running()
        exe = None
        if need_launch:
            prefs = _prefs(context)
            exe = link.find_sable_exe(prefs.sable_path if prefs else "")
            if exe is None:
                self.report({'ERROR'}, "Sable.exe not found: set its path in the Sable Link add-on preferences")
                return {'CANCELLED'}

        error = link.start_link(objects)
        if error:
            self.report({'ERROR'}, error)
            return {'CANCELLED'}

        if need_launch:
            try:
                link.launch_sable(exe, link.link_json_path())
            except OSError as ex:
                self.report({'ERROR'}, "Could not start Sable ({}): {}".format(exe, ex))
                return {'CANCELLED'}
            self.report({'INFO'}, "Sent {} object(s) to Sable (starting Sable)".format(len(objects)))
        else:
            self.report({'INFO'}, "Sent {} object(s) to Sable".format(len(objects)))
        return {'FINISHED'}


class SABLE_OT_stop_link(bpy.types.Operator):
    """Stop sending updates to Sable (Sable keeps running)"""
    bl_idname = "sable.stop_link"
    bl_label = "Stop Sable Link"
    bl_options = {'REGISTER'}

    @classmethod
    def poll(cls, context):
        return link.state.active

    def execute(self, context):
        link.stop_link()
        self.report({'INFO'}, "Sable link stopped")
        return {'FINISHED'}


class SABLE_PT_panel(bpy.types.Panel):
    bl_label = "Sable Link"
    bl_idname = "SABLE_PT_panel"
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = "Sable"

    def draw(self, context):
        layout = self.layout
        state = link.state
        layout.operator(SABLE_OT_send_selection.bl_idname, icon='EXPORT')

        col = layout.column(align=True)
        if state.active:
            objects = link.linked_objects()
            col.label(text="Linked: {} object(s)".format(len(objects)), icon='LINKED')
            for obj in objects[:8]:
                col.label(text="    " + obj.name)
            if len(objects) > 8:
                col.label(text="    ... and {} more".format(len(objects) - 8))
            if state.last_export_time is not None:
                col.label(text="Last export: {} ({:.0f} ms, rev {})".format(
                    time.strftime("%H:%M:%S", time.localtime(state.last_export_time)),
                    state.last_export_seconds * 1000.0, state.revision))
            if state.dirty:
                col.label(text="Update pending...")
        else:
            col.label(text="Not linked", icon='UNLINKED')
        if state.last_error:
            col.label(text=state.last_error, icon='ERROR')
        col.label(text="Sable is running" if link.sable_running() else "Sable not running (from here)",
                  icon='CHECKMARK' if link.sable_running() else 'BLANK1')
        if state.active and state.link_dir:
            col.label(text=os.path.basename(state.link_dir), icon='FILE_FOLDER')

        row = layout.row()
        row.enabled = state.active
        row.operator(SABLE_OT_stop_link.bl_idname, icon='CANCEL')


def _object_menu(self, context):
    self.layout.separator()
    self.layout.operator(SABLE_OT_send_selection.bl_idname, icon='EXPORT')


_classes = (
    SABLE_AP_preferences,
    SABLE_OT_send_selection,
    SABLE_OT_stop_link,
    SABLE_PT_panel,
)


def register():
    for cls in _classes:
        bpy.utils.register_class(cls)
    bpy.types.VIEW3D_MT_object.append(_object_menu)
    link.register()


def unregister():
    link.unregister()
    bpy.types.VIEW3D_MT_object.remove(_object_menu)
    for cls in reversed(_classes):
        bpy.utils.unregister_class(cls)
