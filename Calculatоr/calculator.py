def calculate(first_number, operator, second_number):
    if operator == "+":
        return first_number + second_number
    if operator == "-":
        return first_number - second_number
    if operator == "*":
        return first_number * second_number
    if operator == "/":
        if second_number == 0:
            raise ValueError("Division by zero is not allowed")
        return first_number / second_number
    raise ValueError("Unknown operator")


def main():
    first_number = float(input("Enter the first number: "))
    operator = input("Enter an operator (+, -, *, /): ")
    second_number = float(input("Enter the second number: "))

    try:
        result = calculate(first_number, operator, second_number)
        print(f"Result: {result}")
    except ValueError as error:
        print(f"Error: {error}")


if __name__ == "__main__":
    main()
