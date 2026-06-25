// file: interface_vtable_exports.c
#include "interface_vtable_exports_private.h"

/* Private file declarations. */
void *malloc(uintptr_t size);
static void Widget_IRef_retain(IRef **ctx);
static void Handle_IRef_retain(IRef **ctx);
static const IRef Widget_IRef__storage;
static const IRef Handle_IRef__storage;

static const IRef Widget_IRef__storage = { .retain = Widget_IRef_retain };
const IRef *Widget_IRef = &Widget_IRef__storage;
static const IRef Handle_IRef__storage = { .retain = Handle_IRef_retain };
const IRef *Handle_IRef = &Handle_IRef__storage;
void Widget_retain(Widget *this)
{
}

void Widget_op_initnew(Widget *this)
{
	this->_vt_IRef = Widget_IRef;
}

Widget *Widget_create(void)
{
	Widget *_created0 = (Widget *)(malloc(sizeof(Widget)));
	if ((_created0 != NULL))
	{
		*_created0 = (Widget){0};
		Widget_op_initnew(_created0);
	}
	return _created0;
}

void Handle_retain(Handle *this)
{
}

static void Widget_IRef_retain(IRef **ctx)
{
	Widget *instance = (Widget *)(((uint8_t *)(ctx) - offsetof(Widget, _vt_IRef)));
	Widget_retain(instance);
}

static void Handle_IRef_retain(IRef **ctx)
{
	IRef_Indirect *indirect = (IRef_Indirect *)(ctx);
	Handle *instance = indirect->ctx;
	Handle_retain(instance);
}

// file: interface_vtable_exports.h
#ifndef INTERFACE_VTABLE_EXPORTS_H_
#define INTERFACE_VTABLE_EXPORTS_H_

#include "interface_vtable_exports_private.h"

typedef void (* fn_void_IRefPtrPtr_)(IRef **arg0);
void Widget_retain(Widget *this);
void Widget_op_initnew(Widget *this);
Widget *Widget_create(void);
void Handle_retain(Handle *this);
extern const IRef *Widget_IRef;
extern const IRef *Handle_IRef;

#endif
// file: interface_vtable_exports_private.h
#ifndef INTERFACE_VTABLE_EXPORTS_PRIVATE_H_
#define INTERFACE_VTABLE_EXPORTS_PRIVATE_H_

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

/* Forward declarations. */
typedef struct IRef IRef;
typedef struct Widget Widget;
typedef struct Handle Handle;
typedef struct IRef_Indirect IRef_Indirect;

/* Enums. */

/* Newtypes. */

/* Callable typedefs. */
typedef void (* fn_void_IRefPtrPtr_)(IRef **arg0);

/* Layouts. */
struct IRef
{
	void (* retain)(IRef **arg0);
};
struct Widget
{
	const IRef *_vt_IRef;
};
struct Handle
{
	char _camp_empty;
};
struct IRef_Indirect
{
	const IRef *_vt;
	void *ctx;
};

/* Function declarations. */
void Widget_retain(Widget *this);
void Widget_op_initnew(Widget *this);
Widget *Widget_create(void);
void Handle_retain(Handle *this);

/* Object declarations. */
extern const IRef *Widget_IRef;
extern const IRef *Handle_IRef;


#endif
