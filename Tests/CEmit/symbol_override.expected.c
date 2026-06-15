// file: symbol_override.c
#include "symbol_override_private.h"

/* Private file declarations. */
bool SetWindowTextA(int hWnd, const char *text);
static int privateImpl(void);
static int PrivateNumber;

int MyLibSomeValue = 5;
static int PrivateNumber = 2;
static int privateImpl(void)
{
	return 3;
}

int ControlValue(Control *this)
{
	return 7;
}

int ComputeControlDefaultSize(void)
{
	return 11;
}

int main(void)
{
	Control ctl = (Control){ 0 };
	int val1 = ControlValue(&ctl);
	int val2 = ControlValue(&ctl);
	int val3 = ControlValue(&ctl);
	int size1 = ComputeControlDefaultSize();
	int size2 = ComputeControlDefaultSize();
	int size3 = ComputeControlDefaultSize();
	bool ok1 = SetWindowTextA(0, "a");
	bool ok2 = SetWindowTextA(0, "b");
	int some1 = MyLibSomeValue;
	int some2 = MyLibSomeValue;
	int hidden1 = PrivateNumber;
	int hidden2 = PrivateNumber;
	if ((ok1 && ok2))
	{
		return ((((((((((val1 + val2) + val3) + size1) + size2) + size3) + some1) + some2) + hidden1) + hidden2) + privateImpl());
	}
	return -1;
}

// file: symbol_override.h
#ifndef SYMBOL_OVERRIDE_H_
#define SYMBOL_OVERRIDE_H_

#include "symbol_override_private.h"

int ControlValue(Control *this);
int ComputeControlDefaultSize(void);
int main(void);
extern int MyLibSomeValue;

#endif
// file: symbol_override_private.h
#ifndef SYMBOL_OVERRIDE_PRIVATE_H_
#define SYMBOL_OVERRIDE_PRIVATE_H_

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

/* Forward declarations. */
typedef struct Control Control;

/* Newtypes. */

/* Enums. */

/* Layouts. */
struct Control
{
	char _camp_empty;
};

/* Function declarations. */
int ControlValue(Control *this);
int ComputeControlDefaultSize(void);
int main(void);

/* Object declarations. */
extern int MyLibSomeValue;


#endif
